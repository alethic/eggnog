using System;
using System.Collections.Generic;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// A rule as one scheduler steps it: egglog's <c>SchedulerRuleInfo</c> (<c>src/scheduler.rs</c>), the rule split in
    /// two over a worklist. Its query rule is the rule's body, semi-naive, whose head collects the values of the
    /// variables the rule's head uses into the rule's slot; its action rule matches the worklist, runs the rule's head
    /// and deletes the worklist row it fired. Built once per scheduler and rule, and shared by every scope.
    /// </summary>
    /// <remarks>
    /// As egglog at 5e655cae builds it, the query matches subsumed rows too (<see cref="RuleProgram.IncludeSubsumed"/>).
    /// The worklist is context-private, so a child's is its own and deleting from it is allowed where a rule may not
    /// delete; its rows are canonical when staged and deleted by the bits the action rule matched.
    /// </remarks>
    internal sealed class ScheduledRule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        ScheduledRule(CompiledRule compiled, int slot, IReadOnlyList<string> variables, ColumnType[] types, int[] classColumns,
            TableHandle worklist, RuleVariants query, RuleVariants action)
        {
            Compiled = compiled;
            Slot = slot;
            Variables = variables;
            Types = types;
            ClassColumns = classColumns;
            Worklist = worklist;
            Query = query;
            Action = action;
        }

        /// <summary>
        /// Gets the rule, as compiled.
        /// </summary>
        public CompiledRule Compiled { get; }

        /// <summary>
        /// Gets the rule's slot: the key of its query's buffer in <see cref="ScopeRuntime.SchedulerMatches"/>, unique in
        /// the graph.
        /// </summary>
        public int Slot { get; }

        /// <summary>
        /// Gets the names of a match's values: the variables the head uses, in first-use order.
        /// </summary>
        public IReadOnlyList<string> Variables { get; }

        /// <summary>
        /// Gets the column types of a match's values.
        /// </summary>
        public ColumnType[] Types { get; }

        /// <summary>
        /// Gets the columns of a match that hold classes, which a step canonicalizes in the matches it offers: egglog's
        /// held-back matches keep the ids they were found with (defect 9 of <c>history/egglog-engine-map.md</c>).
        /// </summary>
        public int[] ClassColumns { get; }

        /// <summary>
        /// Gets the worklist: a context-private relation of the matches chosen for one step.
        /// </summary>
        public TableHandle Worklist { get; }

        /// <summary>
        /// Gets the query rule's variants, of which the semi-naive one runs.
        /// </summary>
        public RuleVariants Query { get; }

        /// <summary>
        /// Gets the action rule's variants, of which the naive one runs; its statistics are the rule's.
        /// </summary>
        public RuleVariants Action { get; }

        /// <summary>
        /// Builds the scheduled form of <paramref name="compiled"/> for <paramref name="scheduler"/>, defining its
        /// worklist. Called under the graph's definition lock.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="scheduler">The scheduler.</param>
        /// <param name="compiled">The rule.</param>
        /// <param name="slot">The rule's slot.</param>
        /// <returns>The scheduled rule.</returns>
        /// <exception cref="ArgumentException">The head uses no variable of the body.</exception>
        /// <exception cref="InvalidOperationException">Another thread is using the writable root.</exception>
        public static ScheduledRule Create(EGraph graph, RuleScheduler scheduler, CompiledRule compiled, int slot)
        {
            var program = compiled.Program;
            var free = FreeRegisters(program);
            if (free.Count == 0)
                throw new ArgumentException(NoFreeVariables(compiled.Rule));

            var names = new string[free.Count];
            var types = new ColumnType[free.Count];
            var operands = new Operand[free.Count];
            var classColumns = new List<int>();
            for (var i = 0; i < free.Count; i++)
            {
                var register = program.Registers[free[i]];
                names[i] = string.IsNullOrEmpty(register.Name) ? "$" + free[i].ToString(CultureInfo.InvariantCulture) : register.Name;
                types[i] = register.Type;
                operands[i] = Operand.Register(free[i], register.Type);
                if (register.Type.InvolvesClasses)
                    classColumns.Add(i);
            }

            var description = compiled.Variants.Description + "@" + scheduler.Name;
            var worklist = graph.TakeWorklist("$worklist:" + description, types);
            var query = new RuleProgram(program.Name, program.Registers, program.Atoms, program.Steps,
                [new HeadOp(HeadOpKind.Collect, null, operands, slot, null)], program.WrittenIdConstants, program.IdConstantOrigins,
                free.ToArray(), false, program.ReadScopes)
            {
                IncludeSubsumed = true,
            };

            var head = new HeadOp[program.Head.Length + 1];
            program.Head.CopyTo(head, 0);
            head[^1] = new HeadOp(HeadOpKind.Delete, worklist, operands, -1, null);
            var action = new RuleProgram(program.Name, program.Registers, [new ProgramAtom(worklist, null, operands)], [], head,
                program.WrittenIdConstants, program.IdConstantOrigins, program.NamedRegisters, program.WritesAnalysis, []);
            return new ScheduledRule(compiled, slot, Array.AsReadOnly(names), types, classColumns.ToArray(), worklist,
                new RuleVariants(query, null, description + ":query", false),
                new RuleVariants(action, compiled.Rule, description + ":action", true));
        }

        /// <summary>
        /// Gets why <paramref name="rule"/> cannot be scheduled when its head uses no variable of its body.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <returns>The message.</returns>
        public static string NoFreeVariables(Rule rule) =>
            $"Rule '{rule.Name}' cannot be scheduled: its head uses no variable of its body. egglog at 5e655cae panics on such a rule "
            + "(a match has no value to hold); egglog 3.0.0 fires it.";

        /// <summary>
        /// Gets whether a rule can be scheduled: its head uses a variable of its body, which a match holds.
        /// </summary>
        /// <param name="program">The rule's program.</param>
        /// <returns>Whether the head uses one.</returns>
        public static bool HasFreeVariables(RuleProgram program) => FreeRegisters(program).Count > 0;

        /// <summary>
        /// Gets the body registers the head reads: egglog's <c>head.get_free_vars()</c>, in first-use order.
        /// </summary>
        static List<int> FreeRegisters(RuleProgram program)
        {
            var free = new List<int>();
            foreach (var op in program.Head)
                foreach (var input in op.Inputs)
                    if (input.IsRegister && program.Registers[input.Index].Kind != RegisterKind.Head && free.Contains(input.Index) == false)
                        free.Add(input.Index);

            return free;
        }

    }

}
