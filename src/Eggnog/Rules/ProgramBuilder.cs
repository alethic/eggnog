using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Eggnog
{

    /// <summary>
    /// The mutable state of one compilation: the registers and their union-find, the body's atoms and steps, the class
    /// constants, and the head. <see cref="RuleCompiler"/> drives it; every error is a
    /// <see cref="RuleDefinitionException"/> naming the rule, with a fixed message saying what is wrong.
    /// </summary>
    /// <remarks>
    /// Before the body is resolved, operands name raw registers, which unification merges into classes; a class may be
    /// bound to a constant. Resolution renumbers the classes that stay registers (body registers first, then head
    /// registers), substitutes constants, places the steps, and checks range restriction. One instance per compilation;
    /// not thread-safe.
    /// </remarks>
    internal sealed class ProgramBuilder
    {

        readonly EGraph graph;
        readonly RuleDraft draft;
        readonly Scope? owner;
        readonly string ruleName;
        readonly bool query;
        readonly ScopeTag? licence;
        readonly Term? caseVariable;
        readonly List<ScopeTag> readScopes = new();

        readonly List<ColumnType> rawTypes = new();
        readonly List<string> rawNames = new();
        readonly List<RegisterKind> rawKinds = new();
        readonly List<int> parent = new();
        readonly List<Operand?> constants = new();
        readonly Dictionary<Term, int> declared = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<Term, string> makeVariables = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<Term, int> made = new(ReferenceEqualityComparer.Instance);
        readonly List<(TableHandle Table, ScopeTag? Scope, Operand[] Columns)> rawAtoms = new();
        readonly List<(ProgramStepKind Kind, Primitive? Primitive, Operand[] Arguments, Operand Target)> rawSteps = new();
        readonly Dictionary<string, int> bodyMemo = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> headMemo = new(StringComparer.Ordinal);
        readonly Dictionary<Primitive, int> primitiveIds = new(ReferenceEqualityComparer.Instance);
        readonly List<uint> idCells = new();
        readonly List<ScopeOrigin> idOrigins = new();
        readonly Dictionary<uint, int> idIndex = new();
        readonly List<HeadOp> head = new();

        List<ProgramRegister> registers = new();
        int[] finalOf = Array.Empty<int>();
        bool[] bound = Array.Empty<bool>();
        ProgramAtom[] atoms = Array.Empty<ProgramAtom>();
        ProgramStep[] steps = Array.Empty<ProgramStep>();
        int fresh;

        /// <summary>
        /// Initializes a compilation of <paramref name="draft"/>.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="draft">The draft.</param>
        /// <param name="owner">The rule's owner scope; <c>null</c> for a global rule or a query.</param>
        /// <param name="query">Whether the draft is a query's: its class constants are checked at each call, against the
        /// scope it runs in, rather than against an owner here.</param>
        /// <param name="licence">The rule's licence, under which it is erasing: its own <c>Erasing</c> tag, else its
        /// ruleset's; <c>null</c> for none.</param>
        /// <param name="caseVariable">A split rule's case variable (<see cref="SplitBuilder.Cases{T}"/>): bound per case,
        /// so its use in the body is an error; <c>null</c> for any other draft.</param>
        public ProgramBuilder(EGraph graph, RuleDraft draft, Scope? owner, bool query = false, ScopeTag? licence = null, Term? caseVariable = null)
        {
            this.graph = graph;
            this.draft = draft;
            this.owner = owner;
            this.query = query;
            this.licence = licence;
            this.caseVariable = caseVariable;
            ruleName = draft.Name;
        }

        /// <summary>
        /// Compiles the draft into a program: variables, body normalization with scoped reads, range restriction and step
        /// order, the licence, the head and the analysis-writer check; the mirrors of the scoped reads last.
        /// </summary>
        /// <returns>The program.</returns>
        /// <exception cref="RuleDefinitionException">The draft is not a valid rule.</exception>
        public RuleProgram Build()
        {
            var writesAnalysis = CheckRule();
            ReadMirrors();
            var named = new List<int>();
            foreach (var (_, _, variable) in draft.Variables)
            {
                var operand = Resolve(Operand.Register(declared[variable], rawTypes[declared[variable]]));
                named.Add(operand.IsRegister ? operand.Index : -1);
            }

            foreach (var item in draft.Head)
                if (item.Kind == HeadItemKind.Make)
                    named.Add(made.TryGetValue(item.Variable!, out var register) ? register : -1);

            return new RuleProgram(ruleName, registers.ToArray(), atoms, steps, head.ToArray(), idCells.ToArray(), idOrigins.ToArray(),
                named.ToArray(), writesAnalysis, readScopes.ToArray());
        }

        /// <summary>
        /// Checks the draft as <see cref="Build"/> does, short of creating the mirrors of its scoped reads: what a
        /// definition of several rules runs on a rule before it creates any mirror, so that a refused definition
        /// registers none. The builder is spent afterwards.
        /// </summary>
        /// <exception cref="RuleDefinitionException">The draft is not a valid rule.</exception>
        public void Check() => CheckRule();

        /// <summary>
        /// Every check of <see cref="Build"/> before the mirrors: variables, body normalization with scoped reads, range
        /// restriction and step order, the licence, the head and the analysis-writer check.
        /// </summary>
        /// <returns>Whether the rule writes an analysis table.</returns>
        bool CheckRule()
        {
            DeclareVariables();
            foreach (var fact in draft.Body)
                AddBodyFact(fact, null);

            ResolveBody();
            CheckLicence();
            foreach (var item in draft.Head)
                AddHeadItem(item);

            return CheckAnalysisWriters();
        }

        /// <summary>
        /// Compiles a query's draft: the body checked and normalized as a rule's, and a head of one
        /// <see cref="HeadOpKind.Collect"/> of the declared variables in declaration order. Each declared variable must be
        /// bound by the body or by an equality with a constant; a constant-bound one collects the constant.
        /// </summary>
        /// <returns>The program.</returns>
        /// <exception cref="RuleDefinitionException">The draft is not a valid query.</exception>
        public RuleProgram BuildQuery()
        {
            DeclareVariables();
            foreach (var fact in draft.Body)
                AddBodyFact(fact, null);

            ResolveBody();
            var columns = new Operand[draft.Variables.Count];
            for (var i = 0; i < columns.Length; i++)
                columns[i] = EvalHead(draft.Variables[i].Variable);

            head.Add(new HeadOp(HeadOpKind.Collect, null, columns, -1, null));
            ReadMirrors();
            var named = new int[columns.Length];
            for (var i = 0; i < named.Length; i++)
                named[i] = columns[i].IsRegister ? columns[i].Index : -1;

            return new RuleProgram(ruleName, registers.ToArray(), atoms, steps, head.ToArray(), idCells.ToArray(), idOrigins.ToArray(),
                named, false, readScopes.ToArray());
        }

        /// <summary>
        /// Compiles a split rule's matcher: the body checked and normalized as a rule's, and a head of one
        /// <see cref="HeadOpKind.Collect"/> of <paramref name="columns"/>, the key variables, the set variable and every other
        /// variable an assumption uses. Each must be bound by the body, or equated with a constant, which the collector then
        /// collects. Class constants follow the global-rule rule: only classes from the root.
        /// </summary>
        /// <param name="columns">The declared variables to collect, in column order.</param>
        /// <returns>The matcher's program.</returns>
        /// <exception cref="RuleDefinitionException">The draft is not a valid split body.</exception>
        public RuleProgram BuildSplit(IReadOnlyList<Term> columns)
        {
            DeclareVariables();
            foreach (var fact in draft.Body)
                AddBodyFact(fact, null);

            ResolveBody();
            var operands = new Operand[columns.Count];
            for (var i = 0; i < operands.Length; i++)
                operands[i] = EvalHead(columns[i]);

            head.Add(new HeadOp(HeadOpKind.Collect, null, operands, -1, null));
            ReadMirrors();
            var named = new int[operands.Length];
            for (var i = 0; i < named.Length; i++)
                named[i] = operands[i].IsRegister ? operands[i].Index : -1;

            return new RuleProgram(ruleName, registers.ToArray(), atoms, steps, head.ToArray(), idCells.ToArray(), idOrigins.ToArray(),
                named, false, readScopes.ToArray());
        }

        /// <summary>
        /// Makes a register per declared variable, in declaration order, and records the <c>Make</c> variables; duplicate
        /// names and unknown types are errors.
        /// </summary>
        void DeclareVariables()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, type, variable) in draft.Variables)
            {
                if (names.Add(name) == false)
                    throw Error($"variable '{name}' is declared twice");

                var resolved = type ?? ResolveType(variable.ClrType);
                declared.Add(variable, NewRegister(resolved, name, RegisterKind.Declared));
            }

            foreach (var item in draft.Head)
            {
                if (item.Kind != HeadItemKind.Make)
                    continue;

                var name = item.Variable!.Shape.Name!;
                if (names.Add(name) == false)
                    throw Error($"variable '{name}' is declared twice");

                ResolveType(item.Variable.ClrType);
                makeVariables.Add(item.Variable, name);
            }
        }

        /// <summary>
        /// Normalizes one body fact into atoms, unifications, constant bindings and steps; inside <see cref="Fact.In"/>,
        /// every table application reads <paramref name="enclosing"/>.
        /// </summary>
        void AddBodyFact(Fact fact, ScopeTag? enclosing)
        {
            switch (fact)
            {
                case RelationFact relation:
                {
                    CheckTable(relation.Table);
                    var (table, scope) = AtomTable(relation.Table, enclosing);
                    var columns = new Operand[relation.Arguments.Length];
                    for (var i = 0; i < columns.Length; i++)
                        columns[i] = FlattenBody(relation.Arguments[i], enclosing);

                    rawAtoms.Add((table, scope, columns));
                    break;
                }
                case EqualityFact equality:
                    Equate(FlattenBody(equality.Left, enclosing), FlattenBody(equality.Right, enclosing));
                    break;
                case NotEqualFact notEqual:
                    rawSteps.Add((ProgramStepKind.NotEqual, null, new[] { FlattenBody(notEqual.Left, enclosing), FlattenBody(notEqual.Right, enclosing) }, default));
                    break;
                case ExistsFact exists:
                    FlattenBody(exists.Application, enclosing);
                    break;
                case ScopedFact scoped:
                    if (enclosing is not null)
                        throw Error("Fact.In cannot be nested");

                    if (ReferenceEquals(scoped.Scope.Graph, graph) == false)
                        throw Error($"scope tag '{scoped.Scope.Name}' belongs to another graph");

                    foreach (var inner in scoped.Facts)
                        AddBodyFact(inner, scoped.Scope);

                    break;
                case FilterFact filter:
                {
                    var shape = filter.Condition.Shape;
                    if (shape.Kind == TermShapeKind.Computation)
                    {
                        var primitive = shape.Primitive!;
                        var (parameters, result) = CheckPrimitive(primitive, body: true);
                        if (result.ClrType != typeof(bool))
                            throw Error($"primitive '{primitive.Name}' returns {result.Name}, not bool, so it cannot filter");

                        var arguments = FlattenArguments(shape.Arguments, parameters, primitive, body: true, enclosing);
                        rawSteps.Add((ProgramStepKind.Filter, primitive, arguments, default));
                    }
                    else
                    {
                        rawSteps.Add((ProgramStepKind.Filter, Primitives.IsTrue, new[] { FlattenBody(filter.Condition, enclosing) }, default));
                    }

                    break;
                }
                default:
                    throw Error($"a {fact.GetType().Name} cannot appear in a rule");
            }
        }

        /// <summary>
        /// Turns a body term into an operand, creating atoms for applications and lets for computations; an application
        /// with a read scope (its handle's, or <paramref name="enclosing"/>) becomes an atom over its mirror.
        /// </summary>
        Operand FlattenBody(Term term, ScopeTag? enclosing)
        {
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Variable:
                {
                    CheckOwner(shape);
                    if (declared.TryGetValue(term, out var register))
                        return Operand.Register(register, rawTypes[register]);

                    if (makeVariables.TryGetValue(term, out var made))
                        throw Error($"head variable '{made}' made by Make appears in When");

                    if (ReferenceEquals(term, caseVariable))
                        throw Error($"case variable '{shape.Name}' is bound per case; use it only in Assume");

                    throw Error($"variable '{shape.Name}' belongs to another rule");
                }
                case TermShapeKind.Constant:
                    return ConstantOperand(shape);
                case TermShapeKind.Application:
                {
                    CheckTable(shape.Table!);
                    var (table, scope) = AtomTable(shape.Table!, enclosing);
                    var columns = new Operand[shape.Arguments.Length + 1];
                    for (var i = 0; i < shape.Arguments.Length; i++)
                        columns[i] = FlattenBody(shape.Arguments[i], enclosing);

                    var key = "a" + table.Index.ToString(CultureInfo.InvariantCulture) + "@" + (scope?.Serial ?? -1).ToString(CultureInfo.InvariantCulture)
                        + Key(columns.AsSpan(0, shape.Arguments.Length));
                    if (bodyMemo.TryGetValue(key, out var shared))
                        return Operand.Register(shared, rawTypes[shared]);

                    var output = NewRegister(table.Output!, "$" + table.Name + (fresh++).ToString(CultureInfo.InvariantCulture), RegisterKind.Fresh);
                    columns[^1] = Operand.Register(output, table.Output!);
                    rawAtoms.Add((table, scope, columns));
                    bodyMemo.Add(key, output);
                    return columns[^1];
                }
                default:
                {
                    var primitive = shape.Primitive!;
                    var (parameters, result) = CheckPrimitive(primitive, body: true);
                    var arguments = FlattenArguments(shape.Arguments, parameters, primitive, body: true, enclosing);
                    var key = "c" + PrimitiveId(primitive).ToString(CultureInfo.InvariantCulture) + Key(arguments);
                    if (bodyMemo.TryGetValue(key, out var shared))
                        return Operand.Register(shared, rawTypes[shared]);

                    var target = NewRegister(result, "$" + primitive.Name + (fresh++).ToString(CultureInfo.InvariantCulture), RegisterKind.Fresh);
                    var operand = Operand.Register(target, result);
                    rawSteps.Add((ProgramStepKind.Let, primitive, arguments, operand));
                    bodyMemo.Add(key, target);
                    return operand;
                }
            }
        }

        /// <summary>
        /// Flattens a computation's arguments, checking each against its parameter's type.
        /// </summary>
        Operand[] FlattenArguments(Term[] terms, ColumnType[] parameters, Primitive primitive, bool body, ScopeTag? enclosing = null)
        {
            var arguments = new Operand[terms.Length];
            for (var i = 0; i < terms.Length; i++)
            {
                arguments[i] = body ? FlattenBody(terms[i], enclosing) : EvalHead(terms[i]);
                if (ReferenceEquals(arguments[i].Type, parameters[i]) == false)
                    throw Error($"primitive '{primitive.Name}' takes {parameters[i].Name} as argument {i + 1}, not {arguments[i].Type.Name}");
            }

            return arguments;
        }

        /// <summary>
        /// Equates two operands: unifies registers, binds a constant, or evaluates two constants now.
        /// </summary>
        void Equate(Operand left, Operand right)
        {
            if (left.IsRegister && right.IsRegister)
                Unify(left.Index, right.Index);
            else if (left.IsRegister)
                Bind(left.Index, right);
            else if (right.IsRegister)
                Bind(right.Index, left);
            else if (ReferenceEquals(left.Type, right.Type) == false)
                throw Error($"variables '{Literal(left)}' ({left.Type.Name}) and '{Literal(right)}' ({right.Type.Name}) are equated");
            else if (SameConstant(left, right) == false)
                throw Error(NeverMatches(left, right));
        }

        /// <summary>
        /// Merges the classes of two raw registers; their types must agree, and their constants, if both have one.
        /// </summary>
        void Unify(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb)
                return;

            if (ReferenceEquals(rawTypes[ra], rawTypes[rb]) == false)
                throw Error($"variables '{rawNames[a]}' ({rawTypes[a].Name}) and '{rawNames[b]}' ({rawTypes[b].Name}) are equated");

            var ca = constants[ra];
            var cb = constants[rb];
            if (ca is { } x && cb is { } y && SameConstant(x, y) == false)
                throw Error(NeverMatches(x, y));

            var root = Math.Min(ra, rb);
            var child = Math.Max(ra, rb);
            parent[child] = root;
            constants[root] = ca ?? cb;
        }

        /// <summary>
        /// Binds a raw register's class to a constant.
        /// </summary>
        void Bind(int register, Operand constant)
        {
            var root = Find(register);
            if (ReferenceEquals(rawTypes[root], constant.Type) == false)
                throw Error($"variables '{rawNames[register]}' ({rawTypes[register].Name}) and '{Literal(constant)}' ({constant.Type.Name}) are equated");

            if (constants[root] is { } existing && SameConstant(existing, constant) == false)
                throw Error(NeverMatches(existing, constant));

            constants[root] = constant;
        }

        /// <summary>
        /// Resolves the register classes, places the steps in rounds, and checks that every step's inputs are bound (range
        /// restriction).
        /// </summary>
        void ResolveBody()
        {
            var count = rawTypes.Count;
            finalOf = new int[count];
            Array.Fill(finalOf, -1);
            registers = new List<ProgramRegister>();
            for (var i = 0; i < count; i++)
            {
                var root = Find(i);
                if (constants[root] is not null)
                    continue;

                if (finalOf[root] < 0)
                {
                    finalOf[root] = registers.Count;
                    registers.Add(new ProgramRegister(rawTypes[root], rawNames[root], rawKinds[root]));
                }

                finalOf[i] = finalOf[root];
            }

            atoms = new ProgramAtom[rawAtoms.Count];
            for (var i = 0; i < atoms.Length; i++)
                atoms[i] = new ProgramAtom(rawAtoms[i].Table, rawAtoms[i].Scope, rawAtoms[i].Columns.Select(Resolve).ToArray());

            var pending = new List<ProgramStep>(rawSteps.Count);
            foreach (var (kind, primitive, arguments, target) in rawSteps)
                pending.Add(new ProgramStep(kind, primitive, arguments.Select(Resolve).ToArray(), kind == ProgramStepKind.Let ? Resolve(target) : default));

            bound = new bool[registers.Count];
            foreach (var atom in atoms)
                foreach (var column in atom.Columns)
                    if (column.IsRegister)
                        bound[column.Index] = true;

            // Rounds: each scans the pending steps in declaration order and places every step whose inputs were bound when
            // the round began; the targets the round's lets bind count from the next round.
            var placed = new List<ProgramStep>(pending.Count);
            var round = new List<ProgramStep>();
            while (pending.Count > 0)
            {
                round.Clear();
                foreach (var step in pending)
                    if (step.Arguments.All(IsBound))
                        round.Add(step);

                if (round.Count == 0)
                    break;

                foreach (var step in round)
                {
                    if (step.Kind == ProgramStepKind.Let && step.Target.IsRegister)
                        bound[step.Target.Index] = true;

                    placed.Add(step);
                    pending.Remove(step);
                }
            }

            if (pending.Count > 0)
                throw Error($"variable '{UnboundName(pending[0], pending, 0)}' is not bound by any When clause");

            steps = placed.ToArray();
        }

        /// <summary>
        /// Names the first unbound input of an unplaced step, following generated registers to the step that would have
        /// bound them.
        /// </summary>
        string UnboundName(ProgramStep step, List<ProgramStep> pending, int depth)
        {
            foreach (var argument in step.Arguments)
            {
                if (IsBound(argument))
                    continue;

                var register = registers[argument.Index];
                if (register.Kind == RegisterKind.Fresh && depth < pending.Count)
                {
                    foreach (var other in pending)
                        if (other.Kind == ProgramStepKind.Let && other.Target.IsRegister && other.Target.Index == argument.Index)
                            return UnboundName(other, pending, depth + 1);
                }

                return register.Name;
            }

            return "?";
        }

        /// <summary>
        /// Processes one head item.
        /// </summary>
        void AddHeadItem(HeadItem item)
        {
            switch (item.Kind)
            {
                case HeadItemKind.Then:
                    AddHeadFact(item.Fact!);
                    break;
                case HeadItemKind.Make:
                {
                    var operand = EvalHead(item.Left!);
                    made[item.Variable!] = operand.Index;
                    break;
                }
                case HeadItemKind.Subsume:
                    AddSubsume(item);
                    break;
                case HeadItemKind.Delete:
                    AddDelete(item);
                    break;
                case HeadItemKind.Callback:
                    AddCallback(item.Callback!);
                    break;
                default:
                    // A rewrite's right side is made, and a function application there would be read, which egglog
                    // refuses in an action (LookupInRuleDisallowed, src/typechecking.rs:896-904); here it would be a write.
                    if (item.Right!.Shape is { Kind: TermShapeKind.Application, Table.Kind: TableKind.Function })
                        throw Error($"a rewrite's right side is made, and '{item.Right.Shape.Table.Name}' is a function, which a head does not read");

                    AddHeadFact(new EqualityFact(item.Variable!, item.Right));
                    break;
            }
        }

        /// <summary>
        /// Turns a rule callback into a <see cref="HeadOpKind.Callback"/> over the variables it sees: each declared
        /// variable the body binds, and each <c>Make</c> variable made before it, in declaration order.
        /// </summary>
        void AddCallback(RuleCallbackDefinition callback)
        {
            var variables = new List<Term>();
            var inputs = new List<Operand>();
            foreach (var (_, _, variable) in draft.Variables)
            {
                if (declared.TryGetValue(variable, out var raw) == false)
                    continue;

                var operand = Resolve(Operand.Register(raw, rawTypes[raw]));
                if (operand.IsRegister && bound[operand.Index] == false)
                    continue;

                variables.Add(variable);
                inputs.Add(operand);
            }

            foreach (var (variable, register) in made)
            {
                variables.Add(variable);
                inputs.Add(Operand.Register(register, registers[register].Type));
            }

            head.Add(new HeadOp(HeadOpKind.Callback, null, inputs.ToArray(), -1, null)
            {
                Callback = callback,
                CallbackVariables = variables.ToArray(),
            });
        }

        /// <summary>
        /// Turns a subsumption into a <see cref="HeadOpKind.Subsume"/> of the key of a subsumable relation row or
        /// constructor application. The key's terms are evaluated as head terms (egglog lowers <c>(subsume (f
        /// args...))</c> with the arguments evaluated as actions too), except for a rewrite's left side, whose
        /// arguments are the operands the body bound when it matched it: so the row the body matched is the one
        /// subsumed, and a left side that reads a function, which a head may not, is subsumed too (egglog's action
        /// would look the function up).
        /// </summary>
        void AddSubsume(HeadItem item)
        {
            TableHandle table;
            Term[] arguments;
            if (item.Fact is { } fact)
            {
                if (fact is ScopedFact scoped)
                    throw Error($"head facts are writes; Fact.In('{scoped.Scope.Name}') is read-only");

                if (fact is not RelationFact relation)
                    throw Error($"Subsume takes relation rows and constructor terms; {fact} is not a relation row");

                table = relation.Table;
                arguments = relation.Arguments;
            }
            else
            {
                var shape = item.Left!.Shape;
                if (shape.Kind != TermShapeKind.Application)
                {
                    // egglog: "Subsumed rewrite must have a function call on the lhs" (src/ast/desugar.rs:320-333).
                    throw Error(item.Variable is null
                        ? $"Subsume takes relation rows and constructor terms; {item.Left} is not an application"
                        : $"a subsuming rewrite's left side must be a constructor application, not {item.Left}");
                }

                table = shape.Table!;
                arguments = shape.Arguments;
            }

            CheckTable(table);
            CheckWritable(table);
            if (table.Subsumable == false)
                throw Error($"'{table.Name}' is not subsumable: only relations and constructors are, unless defined with TableOptions.Subsumable false");

            Operand[] inputs;
            if (item.Fact is null && item.Variable is not null)
            {
                inputs = BodyOperands(arguments);
            }
            else
            {
                inputs = new Operand[arguments.Length];
                for (var i = 0; i < inputs.Length; i++)
                    inputs[i] = EvalHead(arguments[i]);
            }

            head.Add(new HeadOp(HeadOpKind.Subsume, table, inputs, -1, null));
        }

        /// <summary>
        /// Turns a deletion into a <see cref="HeadOpKind.Delete"/> of the key of a relation row or a function or
        /// constructor application. The key's terms are evaluated as head terms, as egglog lowers <c>(delete (f
        /// args...))</c> with the arguments evaluated as actions.
        /// </summary>
        void AddDelete(HeadItem item)
        {
            TableHandle table;
            Term[] arguments;
            if (item.Fact is { } fact)
            {
                if (fact is ScopedFact scoped)
                    throw Error($"head facts are writes; Fact.In('{scoped.Scope.Name}') is read-only");

                if (fact is not RelationFact relation)
                    throw Error($"Delete takes relation rows and function or constructor applications; {fact} is not a relation row");

                table = relation.Table;
                arguments = relation.Arguments;
            }
            else
            {
                var shape = item.Left!.Shape;
                if (shape.Kind != TermShapeKind.Application)
                    throw Error($"Delete takes relation rows and function or constructor applications; {item.Left} is not an application");

                table = shape.Table!;
                arguments = shape.Arguments;
            }

            CheckTable(table);
            CheckWritable(table);
            if (ReferenceEquals(table, graph.Contradiction))
                throw Error($"'{table.Name}' is the graph's contradiction, which a deletion does not undo");

            var inputs = new Operand[arguments.Length];
            for (var i = 0; i < inputs.Length; i++)
                inputs[i] = EvalHead(arguments[i]);

            head.Add(new HeadOp(HeadOpKind.Delete, table, inputs, -1, null));
        }

        /// <summary>
        /// Gets the operands the body bound for terms it flattened, such as the arguments of a rewrite's left side.
        /// Flattening them again finds each in the body's memo, so it creates no atom and no step.
        /// </summary>
        /// <exception cref="InvalidOperationException">A term was not flattened by the body.</exception>
        Operand[] BodyOperands(Term[] terms)
        {
            var atomCount = rawAtoms.Count;
            var stepCount = rawSteps.Count;
            var operands = new Operand[terms.Length];
            for (var i = 0; i < operands.Length; i++)
                operands[i] = Resolve(FlattenBody(terms[i], null));

            if (rawAtoms.Count != atomCount || rawSteps.Count != stepCount)
                throw new InvalidOperationException($"Rule '{ruleName}': a term of the head was not flattened by the body.");

            return operands;
        }

        /// <summary>
        /// Turns a head fact into writes and unions.
        /// </summary>
        void AddHeadFact(Fact fact)
        {
            switch (fact)
            {
                case RelationFact relation:
                {
                    CheckTable(relation.Table);
                    CheckWritable(relation.Table);
                    var inputs = new Operand[relation.Arguments.Length];
                    for (var i = 0; i < inputs.Length; i++)
                        inputs[i] = EvalHead(relation.Arguments[i]);

                    head.Add(new HeadOp(HeadOpKind.Insert, relation.Table, inputs, -1, null));
                    break;
                }
                case EqualityFact equality:
                {
                    var left = equality.Left.Shape;
                    var right = equality.Right.Shape;
                    if (IsApplication(left, TableKind.Function))
                        Write(left, equality.Right);
                    else if (IsApplication(right, TableKind.Function))
                        Write(right, equality.Left);
                    else if (IsApplication(left, TableKind.Constructor) && IsApplication(right, TableKind.Constructor) == false)
                        Write(left, equality.Right);
                    else if (IsApplication(right, TableKind.Constructor) && IsApplication(left, TableKind.Constructor) == false)
                        Write(right, equality.Left);
                    else if (ResolveType(equality.Left.ClrType).Kind == ColumnKind.Sort)
                        head.Add(new HeadOp(HeadOpKind.Union, null, new[] { EvalHead(equality.Left), EvalHead(equality.Right) }, -1, null));
                    else
                        throw Error($"equality of two values that is not a function write: {equality.Left} = {equality.Right}");

                    break;
                }
                case NotEqualFact:
                    throw Error("head facts are writes; 'IsNot' is a filter");
                case ExistsFact:
                    throw Error("head facts are writes; 'Exists' is a filter");
                case FilterFact:
                    throw Error("head facts are writes; 'True' is a filter");
                case ScopedFact scoped:
                    throw Error($"head facts are writes; Fact.In('{scoped.Scope.Name}') is read-only");
                default:
                    throw Error($"a {fact.GetType().Name} cannot appear in a rule");
            }
        }

        /// <summary>
        /// Writes a function or constructor row: the application's keys, then <paramref name="value"/>.
        /// </summary>
        void Write(TermShape application, Term value)
        {
            var table = application.Table!;
            CheckTable(table);
            CheckWritable(table);
            var inputs = new Operand[application.Arguments.Length + 1];
            for (var i = 0; i < application.Arguments.Length; i++)
                inputs[i] = EvalHead(application.Arguments[i]);

            inputs[^1] = EvalHead(value);
            head.Add(new HeadOp(HeadOpKind.Write, table, inputs, -1, null));
        }

        /// <summary>
        /// Turns a head term into an operand: a bound register, a constant, a get-or-add or a computation.
        /// </summary>
        Operand EvalHead(Term term)
        {
            var shape = term.Shape;
            switch (shape.Kind)
            {
                case TermShapeKind.Variable:
                {
                    CheckOwner(shape);
                    if (declared.TryGetValue(term, out var raw))
                    {
                        var operand = Resolve(Operand.Register(raw, rawTypes[raw]));
                        if (operand.IsRegister && bound[operand.Index] == false)
                            throw Error($"variable '{shape.Name}' is not bound by any When clause");

                        return operand;
                    }

                    if (makeVariables.TryGetValue(term, out var name))
                    {
                        if (made.TryGetValue(term, out var register))
                            return Operand.Register(register, registers[register].Type);

                        throw Error($"head variable '{name}' is used before its Make");
                    }

                    throw Error($"variable '{shape.Name}' belongs to another rule");
                }
                case TermShapeKind.Constant:
                    return ConstantOperand(shape);
                case TermShapeKind.Application:
                {
                    var table = shape.Table!;
                    CheckTable(table);
                    CheckWritable(table);
                    if (table.Kind != TableKind.Constructor)
                        throw Error($"actions do not read; match '{table.Name}' in When");

                    var inputs = new Operand[shape.Arguments.Length];
                    for (var i = 0; i < inputs.Length; i++)
                        inputs[i] = EvalHead(shape.Arguments[i]);

                    var key = "g" + table.Index.ToString(CultureInfo.InvariantCulture) + Key(inputs);
                    if (headMemo.TryGetValue(key, out var shared))
                        return Operand.Register(shared, registers[shared].Type);

                    var target = NewHeadRegister(table.Output!, "$" + table.Name);
                    head.Add(new HeadOp(HeadOpKind.GetOrAdd, table, inputs, target, null));
                    headMemo.Add(key, target);
                    return Operand.Register(target, table.Output!);
                }
                default:
                {
                    var primitive = shape.Primitive!;
                    var (parameters, result) = CheckPrimitive(primitive);
                    var inputs = FlattenArguments(shape.Arguments, parameters, primitive, body: false);
                    var key = "c" + PrimitiveId(primitive).ToString(CultureInfo.InvariantCulture) + Key(inputs);
                    if (headMemo.TryGetValue(key, out var shared))
                        return Operand.Register(shared, registers[shared].Type);

                    var target = NewHeadRegister(result, "$" + primitive.Name);
                    head.Add(new HeadOp(HeadOpKind.Compute, null, inputs, target, primitive));
                    headMemo.Add(key, target);
                    return Operand.Register(target, result);
                }
            }
        }

        /// <summary>
        /// Checks that a rule that writes an analysis table reads no fact table and no scoped atom, so analyses depend only
        /// on terms.
        /// </summary>
        /// <returns>Whether the rule writes an analysis table.</returns>
        bool CheckAnalysisWriters()
        {
            TableHandle? written = null;
            foreach (var op in head)
            {
                if (op.Kind is HeadOpKind.Insert or HeadOpKind.Write or HeadOpKind.Subsume or HeadOpKind.Delete && op.Table!.Role == TableRole.Analysis)
                {
                    written = op.Table;
                    break;
                }
            }

            if (written is null)
                return false;

            foreach (var atom in atoms)
            {
                if (atom.Table.Role == TableRole.Fact)
                    throw Error($"rule writes analysis table '{written.Name}' and may read only term and analysis tables; it reads '{SourceName(atom)}'");

                if (atom.Scope is not null)
                    throw Error($"rule writes analysis table '{written.Name}' and may not read scoped atoms; it reads '{SourceName(atom)}@{atom.Scope.Name}'");
            }

            return true;
        }

        /// <summary>
        /// Gets the table an atom over <paramref name="table"/> reads, with its read scope: the handle's own
        /// (<c>In(tag)</c>) or <paramref name="enclosing"/> (<see cref="Fact.In"/>). A scoped read is of a value-only table;
        /// the atom keeps the source table until <see cref="ReadMirrors"/> moves it onto its mirror once every other check
        /// has passed, so that a refused definition registers no mirror. The tag is recorded in the program's read scopes,
        /// in first-use order.
        /// </summary>
        (TableHandle Table, ScopeTag? Scope) AtomTable(TableHandle table, ScopeTag? enclosing)
        {
            var own = table.ReadScope;
            if (own is not null && enclosing is not null && ReferenceEquals(own, enclosing) == false)
                throw Error($"'{table.Name}@{own.Name}' is inside Fact.In('{enclosing.Name}')");

            var scope = own ?? enclosing;
            if (scope is null)
                return (table, null);

            var source = table.Table;
            if (source.IsValueOnly == false)
                throw Error($"scoped reads are limited to tables whose columns are all values; '{source.Name}' has a sort column");

            // A scoped read copies the table into a mirror in the scope that reads it, which below the root only a lattice
            // merge may write.
            if (source is FunctionHandle { Merge: { } merge })
                throw Error($"a scoped read copies the table into a mirror, which only the root may write when the merge is not a lattice; '{source.Name}' has the merge '{merge.Name}', which is not a lattice");

            if (readScopes.Contains(scope) == false)
                readScopes.Add(scope);

            return (source, scope);
        }

        /// <summary>
        /// Moves each scoped atom onto the mirror of its table under its tag, creating the mirrors this definition
        /// reads first. Called once every other check has passed; the creation is all or nothing
        /// (<see cref="MirrorRegistry.GetOrCreateAll"/>), so a refused definition registers no mirror and does not
        /// count as a first read.
        /// </summary>
        /// <exception cref="RuleDefinitionException">A mirror would be new and a live frozen scope carries its tag.</exception>
        /// <exception cref="InvalidOperationException">A mirror would be new and another thread is using the writable root:
        /// a new mirror is a table definition, which a writable root refuses while another thread uses it.</exception>
        void ReadMirrors()
        {
            List<(TableHandle Table, ScopeTag Tag)>? reads = null;
            foreach (var atom in atoms)
                if (atom.Scope is { } tag && (reads ??= new()).Contains((atom.Table, tag)) == false)
                    reads.Add((atom.Table, tag));

            if (reads is null)
                return;

            var mirrors = graph.Mirrors.GetOrCreateAll(reads, ruleName);
            for (var i = 0; i < atoms.Length; i++)
                if (atoms[i].Scope is { } tag)
                    atoms[i] = new ProgramAtom(mirrors[reads.IndexOf((atoms[i].Table, tag))].Handle, tag, atoms[i].Columns);
        }

        /// <summary>
        /// Checks that a head never writes a scoped view: actions write only to the running scope. A rule local to a scope
        /// below the root never runs at the root, so it may not write a function whose merge is not a lattice.
        /// </summary>
        void CheckWritable(TableHandle table)
        {
            if (table.ReadScope is { } tag)
                throw Error($"'{table.Name}@{tag.Name}' is read-only");

            if (owner is { IsRoot: false } && table is FunctionHandle { Merge: { } merge })
                throw Error($"'{table.Name}' has the merge '{merge.Name}', which is not a lattice, so only the root may write it, and a rule local to '{owner.Path}' never runs there");
        }

        /// <summary>
        /// Checks the licence: with a licence and no <c>ReadsOwnFacts</c>, every atom over a fact table reads the licence
        /// scope; <c>ReadsOwnFacts</c> needs a reason and a licence.
        /// </summary>
        void CheckLicence()
        {
            if (draft.OwnFactsReason is { } reason)
            {
                if (string.IsNullOrWhiteSpace(reason))
                    throw Error("ReadsOwnFacts needs a reason");

                if (licence is null)
                    throw Error("rule calls ReadsOwnFacts but has no licence");

                return;
            }

            if (licence is null)
                return;

            foreach (var atom in atoms)
                if (atom.Table.Role == TableRole.Fact && ReferenceEquals(atom.Scope, licence) == false)
                    throw Error($"rule reads fact table '{SourceName(atom)}' outside scope '{licence.Name}'; read it through In({licence.Name}), or call ReadsOwnFacts with the reason that is sound");

            // A full callback reads the running scope's tables, which no licence check sees.
            foreach (var item in draft.Head)
                if (item.Callback is { Full: true })
                    throw Error($"rule reads tables in a full callback, outside scope '{licence.Name}'; "
                        + "call ReadsOwnFacts with the reason that is sound");
        }

        /// <summary>
        /// Gets the name of the table an atom reads: a mirror's source's.
        /// </summary>
        static string SourceName(ProgramAtom atom) => (atom.Table.Source ?? atom.Table).Name;

        /// <summary>
        /// Turns a constant into a value constant or a class constant. A class must name a class of this graph; outside a
        /// query, the scope that returned it must also be committed (for a batch) and one the rule may hold classes from
        /// (<see cref="ClassChecks.AllowedInRule"/>), disposed or not; a refused class of a disposed scope is reported as
        /// disposed.
        /// </summary>
        Operand ConstantOperand(TermShape shape)
        {
            var type = shape.ConstantType ?? ResolveType(shape.ClrType);
            CheckStore(type);

            // egglog has no literal for a container of classes; a rule builds one from terms with its primitives.
            if (type.HoldsClasses)
                throw Error($"a constant {type.Name} holds classes; build it in the rule with its primitives over terms, such as Primitives.VecOf, as egglog has no literal for one");

            if (type.Kind == ColumnKind.Sort)
            {
                var id = type.ClassOf(shape.Constant!);
                var origin = id.Origin;
                if (origin is null)
                    throw Error("a class constant is default(ClassId), which names no class");

                if (ReferenceEquals(origin.Graph, graph) == false)
                    throw Error($"class {id} belongs to another graph");

                // A query's constants are checked at each call, against the scope it runs in.
                if (query)
                    return IdConstant(id, origin, type);

                if (origin.IsBatch && origin.Committed == false)
                    throw Error($"class {id} was made by a Write batch that did not commit");

                var scope = origin.IsBatch ? origin.Parent! : origin;
                if (ClassChecks.AllowedInRule(owner, origin) == false)
                {
                    if (scope.Disposed)
                        throw Error($"class {id} came from scope '{scope.Path}', which is disposed");

                    if (owner is null)
                        throw Error($"class {id} came from scope '{scope.Path}'; a global rule may hold only classes from the root; define the rule on that scope");

                    throw Error($"class {id} came from scope '{scope.Path}', which is not '{owner.Path}' or an ancestor of it");
                }

                return IdConstant(id, origin, type);
            }

            try
            {
                return Operand.Constant(type, type.EncodeObject(shape.Constant!));
            }
            catch (ArgumentException exception)
            {
                throw Error(exception.Message, exception);
            }
        }

        /// <summary>
        /// Gets the operand of a class constant, one index per distinct cell.
        /// </summary>
        Operand IdConstant(ClassId id, ScopeOrigin origin, ColumnType type)
        {
            if (idIndex.TryGetValue(id.Cell, out var index) == false)
            {
                index = idCells.Count;
                idCells.Add(id.Cell);
                idOrigins.Add(origin);
                idIndex.Add(id.Cell, index);
            }

            return Operand.IdConstant(index, type);
        }

        /// <summary>
        /// Gets the column types of a primitive's parameters and result, values or sorts; a primitive of the body must not
        /// write, as egglog rejects a write primitive in a rule's query.
        /// </summary>
        (ColumnType[] Parameters, ColumnType Result) CheckPrimitive(Primitive primitive, bool body = false)
        {
            if (body && primitive.IsWrite)
                throw Error($"primitive '{primitive.Name}' writes the graph, so it runs in the head, not the body, as egglog rejects a write primitive in a rule's query");

            var parameters = new ColumnType[primitive.ParameterTypes.Count];
            for (var i = 0; i < parameters.Length; i++)
                parameters[i] = ResolveType(primitive.ParameterTypes[i]);

            // A primitive may take and return classes, as egglog's take any sort: it sees them as their canonical ids.
            var result = ResolveType(primitive.ResultType);
            return (parameters, result);
        }

        /// <summary>
        /// Gets the column type of a CLR type; an unknown type is an error.
        /// </summary>
        ColumnType ResolveType(Type clrType)
        {
            try
            {
                return graph.Values.TypeOf(clrType);
            }
            catch (ArgumentException exception)
            {
                throw Error($"type {ValueStore.DisplayName(clrType)} is not registered; call {ValueStore.RegisterMethod(clrType)}", exception);
            }
        }

        /// <summary>
        /// Checks that a table belongs to the graph.
        /// </summary>
        void CheckTable(TableHandle table)
        {
            if (ReferenceEquals(table.Graph, graph) == false)
                throw Error($"table '{table.Name}' belongs to another graph");
        }

        /// <summary>
        /// Checks that a column type belongs to the graph.
        /// </summary>
        void CheckStore(ColumnType type)
        {
            if (type.Store is not null && ReferenceEquals(type.Store, graph.Values) == false)
                throw Error($"column type {type.Name} belongs to another graph");
        }

        /// <summary>
        /// Checks that a variable belongs to this rule's builder.
        /// </summary>
        void CheckOwner(TermShape shape)
        {
            if (ReferenceEquals(shape.Owner, draft.Owner) == false)
                throw Error($"variable '{shape.Name}' belongs to another rule");
        }

        /// <summary>
        /// Gets whether a shape is an application of a table of <paramref name="kind"/>.
        /// </summary>
        static bool IsApplication(TermShape shape, TableKind kind) => shape.Kind == TermShapeKind.Application && shape.Table!.Kind == kind;

        /// <summary>
        /// Gets whether an operand is bound: a constant, or a bound register.
        /// </summary>
        bool IsBound(Operand operand) => operand.IsRegister == false || bound[operand.Index];

        /// <summary>
        /// Maps a raw operand to its final form: its class's constant, or the class's final register.
        /// </summary>
        Operand Resolve(Operand operand)
        {
            if (operand.IsRegister == false)
                return operand;

            var root = Find(operand.Index);
            if (constants[root] is { } constant)
                return constant;

            return Operand.Register(finalOf[root], rawTypes[root]);
        }

        /// <summary>
        /// Finds a raw register's class, compressing the path.
        /// </summary>
        int Find(int register)
        {
            var root = register;
            while (parent[root] != root)
                root = parent[root];

            while (parent[register] != root)
            {
                var next = parent[register];
                parent[register] = root;
                register = next;
            }

            return root;
        }

        /// <summary>
        /// Adds a raw body register.
        /// </summary>
        int NewRegister(ColumnType type, string name, RegisterKind kind)
        {
            var index = rawTypes.Count;
            rawTypes.Add(type);
            rawNames.Add(name);
            rawKinds.Add(kind);
            parent.Add(index);
            constants.Add(null);
            return index;
        }

        /// <summary>
        /// Adds a head register, after the body registers.
        /// </summary>
        int NewHeadRegister(ColumnType type, string name)
        {
            var index = registers.Count;
            registers.Add(new ProgramRegister(type, name + index.ToString(CultureInfo.InvariantCulture), RegisterKind.Head));
            return index;
        }

        /// <summary>
        /// Gets a per-compilation id for a primitive instance, for memo keys.
        /// </summary>
        int PrimitiveId(Primitive primitive)
        {
            if (primitiveIds.TryGetValue(primitive, out var id) == false)
            {
                id = primitiveIds.Count;
                primitiveIds.Add(primitive, id);
            }

            return id;
        }

        /// <summary>
        /// Makes a memo key of operands.
        /// </summary>
        static string Key(ReadOnlySpan<Operand> operands)
        {
            var builder = new StringBuilder();
            foreach (var operand in operands)
                builder.Append('|').Append((int)operand.Kind).Append(':').Append(operand.Index).Append(':').Append(operand.Bits);

            return builder.ToString();
        }

        /// <summary>
        /// Gets whether two constant operands may be equal (<c>c1.Is(c2)</c>, directly or through unification). Value
        /// constants compare by cell, now: equal values of one column type have equal cells, so different cells never
        /// match. Class constants are never refused: a global rule runs in every scope a run reaches and a local rule in its
        /// owner's descendants, and a child, which starts as a copy of its parent, may union two classes that differ where
        /// the rule's constants come from. When the two are already one class there (in the owner for a local rule; in the
        /// root for a global rule) and that can be read now, the union holds in every scope the rule runs in, and the caller
        /// keeps the first constant. Otherwise (different classes, a scope that cannot be read without racing another
        /// thread, or a query, whose classes may come from any scope and are checked against the scope it runs in) an
        /// <see cref="ProgramStepKind.Equal"/> step keeps only the matches where the two constants are one class in the
        /// running context, whose canonical forms the engine substitutes, and this returns <c>true</c>. So a definition's
        /// outcome never depends on another thread. Cost: one <c>Find</c> per class at definition, and one comparison per
        /// match when the step is added.
        /// </summary>
        bool SameConstant(Operand x, Operand y)
        {
            if (x.Kind != y.Kind)
                return false;

            if (x.Kind == OperandKind.Constant)
                return x.Bits == y.Bits;

            if (x.Index == y.Index)
                return true;

            if (TryCompareClasses(idCells[x.Index], idCells[y.Index]) == true)
                return true;

            rawSteps.Add((ProgramStepKind.Equal, null, new[] { x, y }, default));
            return true;
        }

        /// <summary>
        /// Compares two class cells where the rule's constants come from, if that can be read now without racing another
        /// thread: a local rule's owner, whose guard the definition holds; the root when it is frozen (lock-free) or when
        /// this thread can take its guard. A query never compares here. Only a <c>true</c> answer is used (to drop the
        /// second constant); any other answer leaves the comparison to run time, so a definition's outcome never depends on
        /// whether another thread is using the root.
        /// </summary>
        /// <returns>Whether the cells are one class; <c>null</c> when the comparison is left to run time.</returns>
        bool? TryCompareClasses(uint a, uint b)
        {
            if (query)
                return null;

            var scope = owner ?? graph.Root;
            if (owner is null && scope.IsFrozen == false)
            {
                if (scope.Guard.TryEnter() == false)
                    return null;

                try
                {
                    return Compare(scope, a, b);
                }
                finally
                {
                    scope.Guard.Exit();
                }
            }

            return Compare(scope, a, b);
        }

        /// <summary>
        /// Compares two class cells by their canonical forms in <paramref name="scope"/>, which the caller may read.
        /// </summary>
        /// <returns>Whether they are one class; <c>null</c> when the scope cannot answer now (not clean, faulted, or inside
        /// an engine call of its domain).</returns>
        static bool? Compare(Scope scope, uint a, uint b)
        {
            try
            {
                return scope.Engine.Find(a) == scope.Engine.Find(b);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Renders a constant operand.
        /// </summary>
        string Literal(Operand operand) => operand.Kind switch
        {
            OperandKind.Constant => operand.Type.FormatCell(operand.Bits),
            OperandKind.IdConstant => "#" + idCells[operand.Index].ToString(CultureInfo.InvariantCulture),
            _ => rawNames[operand.Index],
        };

        /// <summary>
        /// The message of two different constants equated.
        /// </summary>
        string NeverMatches(Operand x, Operand y) => $"never matches: {Literal(x)} = {Literal(y)} is false";

        /// <summary>
        /// Makes a definition error naming the rule.
        /// </summary>
        RuleDefinitionException Error(string message, Exception? inner = null) => new(ruleName, message, inner);

    }

}
