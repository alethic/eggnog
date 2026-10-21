using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Builds one engine rule from a program with the bridge's rule builder.
    /// </summary>
    /// <remarks>
    /// Atoms are lowered in program order (the engine's variant order), each relation atom ending with the unit constant;
    /// then the body steps as <c>QueryPrim</c>s in placement order, which the bridge runs after the join on every match;
    /// then the head operations in builder-call order (a query's or split matcher's head is one collector). Every call
    /// site is registered as an external function owned by the variant. Called under the variants' lock; the bridge's
    /// definitions are thread-safe (published copy-on-write).
    /// </remarks>
    internal static class RuleLowering
    {

        /// <summary>
        /// Lowers a program.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="program">The program.</param>
        /// <param name="rule">The rule, for statistics; <c>null</c> for a query, a split matcher or a copy rule.</param>
        /// <param name="semiNaive">Whether to build a semi-naive engine rule.</param>
        /// <param name="constants">The values to lower the class constants with, by <see cref="OperandKind.IdConstant"/>
        /// index.</param>
        /// <param name="description">The variant's description, unique in the graph: it keys the engine's
        /// statistics.</param>
        /// <returns>The variant.</returns>
        public static LoweredRule Lower(EGraph graph, RuleProgram program, Rule? rule, bool semiNaive, ReadOnlySpan<uint> constants, string description)
        {
            var engine = graph.Engine;
            var unitType = graph.Unit.EngineType;
            var unit = Relations.RuleEntry.Const(new Relations.Value(graph.Unit.Unit), unitType);
            var owned = new List<Relations.ExternalFunctionId>();
            var lowered = constants.ToArray();
            try
            {
                var builder = engine.NewRule(description, semiNaive);
                var variables = new Relations.RuleVariable?[program.Registers.Length];

                // A variable for every body register an atom or a step uses, made before the first atom.
                foreach (var atom in program.Atoms)
                    foreach (var column in atom.Columns)
                        Declare(builder, program, variables, column);

                foreach (var step in program.Steps)
                {
                    foreach (var argument in step.Arguments)
                        Declare(builder, program, variables, argument);

                    if (step.Kind == ProgramStepKind.Let)
                        Declare(builder, program, variables, step.Target);
                }

                foreach (var atom in program.Atoms)
                {
                    var columns = new Relations.RuleEntry[atom.Columns.Length + (atom.Table.IsRelation ? 1 : 0)];
                    for (var i = 0; i < atom.Columns.Length; i++)
                        columns[i] = Entry(atom.Columns[i], variables, lowered);

                    if (atom.Table.IsRelation)
                        columns[^1] = unit;

                    // Rows that are not subsumed, as egglog's rules match (src/lib.rs:2525-2537), or every row for a
                    // scheduler's query; a table without a subsumption column ignores the flag.
                    builder.QueryTable(atom.Table.Function, columns, program.IncludeSubsumed ? null : atom.SubsumedOnly);
                }

                foreach (var step in program.Steps)
                {
                    var arguments = new Relations.RuleEntry[step.Arguments.Length + 1];
                    for (var i = 0; i < step.Arguments.Length; i++)
                        arguments[i] = Entry(step.Arguments[i], variables, lowered);

                    switch (step.Kind)
                    {
                        case ProgramStepKind.Let:
                        {
                            var call = new PrimitiveCall(graph, program.Name, step.Primitive!, Types(step.Arguments), step.Target.Type, Names(program, step.Arguments), true);
                            var function = engine.RegisterExternalFunction(call);
                            owned.Add(function);
                            arguments[^1] = Entry(step.Target, variables, lowered);
                            builder.QueryPrim(function, arguments, step.Target.Type.EngineType);
                            break;
                        }
                        case ProgramStepKind.Filter:
                        {
                            var call = new FilterCall(graph, program.Name, step.Primitive!, Types(step.Arguments), Names(program, step.Arguments));
                            var function = engine.RegisterExternalFunction(call);
                            owned.Add(function);
                            arguments[^1] = builder.NewVar(unitType);
                            builder.QueryPrim(function, arguments, unitType);
                            break;
                        }
                        case ProgramStepKind.Equal:
                            arguments[^1] = builder.NewVar(unitType);
                            builder.QueryPrim(graph.Equal, arguments, unitType);
                            break;
                        default:
                            arguments[^1] = builder.NewVar(unitType);
                            builder.QueryPrim(graph.NotEqual, arguments, unitType);
                            break;
                    }
                }

                var head = new Relations.RuleVariable?[program.Registers.Length];
                MatchCollector? collector = null;
                foreach (var op in program.Head)
                {
                    var inputs = new Relations.RuleEntry[op.Inputs.Length];
                    for (var i = 0; i < inputs.Length; i++)
                        inputs[i] = HeadEntry(op.Inputs[i], program, variables, head, lowered);

                    switch (op.Kind)
                    {
                        case HeadOpKind.Insert:
                        {
                            var row = new Relations.RuleEntry[inputs.Length + 1];
                            inputs.CopyTo(row, 0);
                            row[^1] = unit;
                            builder.Set(op.Table!.Function, row);
                            break;
                        }
                        case HeadOpKind.Write:
                            builder.Set(op.Table!.Function, inputs);
                            break;
                        case HeadOpKind.GetOrAdd:
                        {
                            var name = program.Name;
                            var table = op.Table!.Name;
                            head[op.Target] = builder.Lookup(op.Table.Function, inputs, () => $"rule '{name}': get-or-add of '{table}' failed");
                            break;
                        }
                        case HeadOpKind.Union:
                            builder.Union(inputs[0], inputs[1]);
                            break;
                        case HeadOpKind.Subsume:
                            builder.Subsume(op.Table!.Function, inputs);
                            break;
                        case HeadOpKind.Delete:
                            builder.Remove(op.Table!.Function, inputs);
                            break;
                        case HeadOpKind.Collect:
                        {
                            // The collector never fails, so its panic message is never built; the call stages nothing. A
                            // scheduler's query collects into its rule's slot (the op's target), a query or matcher into -1.
                            collector = new MatchCollector(graph, inputs.Length, graph.Unit.Unit, op.Target);
                            var function = engine.RegisterExternalFunction(collector);
                            owned.Add(function);
                            var name = program.Name;
                            builder.CallExternalFunc(function, inputs, unitType, () => $"'{name}': collector failed");
                            break;
                        }
                        default:
                        {
                            var resultType = program.Registers[op.Target].Type;
                            var call = new PrimitiveCall(graph, program.Name, op.Primitive!, Types(op.Inputs), resultType, Names(program, op.Inputs), false);
                            var function = engine.RegisterExternalFunction(call);
                            owned.Add(function);
                            var name = program.Name;
                            var primitive = op.Primitive!.Name;
                            head[op.Target] = builder.CallExternalFunc(function, inputs, resultType.EngineType, () => $"rule '{name}': primitive '{primitive}' failed");
                            break;
                        }
                    }
                }

                var id = builder.Build();
                var variant = new LoweredRule(id, description, semiNaive, lowered, owned.ToArray(), rule, collector);
                graph.VariantsByDescription[description] = variant;
                return variant;
            }
            catch
            {
                foreach (var function in owned)
                    engine.FreeExternalFunction(function);

                throw;
            }
        }

        /// <summary>
        /// Makes the engine variable of a body register operand, once.
        /// </summary>
        static void Declare(Relations.EGraphRuleBuilder builder, RuleProgram program, Relations.RuleVariable?[] variables, Operand operand)
        {
            if (operand.IsRegister && variables[operand.Index] is null)
            {
                var register = program.Registers[operand.Index];
                variables[operand.Index] = builder.NewVarNamed(register.Type.EngineType, register.Name);
            }
        }

        /// <summary>
        /// Gets the entry of a body operand: a register's engine variable, a value constant, or a class constant as an Id
        /// constant.
        /// </summary>
        static Relations.RuleEntry Entry(Operand operand, Relations.RuleVariable?[] variables, uint[] constants) => operand.Kind switch
        {
            OperandKind.Register => variables[operand.Index] ?? throw new InvalidOperationException($"Register {operand.Index} has no engine variable."),
            OperandKind.Constant => Relations.RuleEntry.Const(new Relations.Value(operand.Bits), operand.Type.EngineType),
            _ => Relations.RuleEntry.Const(new Relations.Value(constants[operand.Index]), Relations.ColumnTy.Id),
        };

        /// <summary>
        /// Gets the entry of a head operand: a head register is the variable its action defined.
        /// </summary>
        static Relations.RuleEntry HeadEntry(Operand operand, RuleProgram program, Relations.RuleVariable?[] variables, Relations.RuleVariable?[] head, uint[] constants)
        {
            if (operand.IsRegister && program.Registers[operand.Index].Kind == RegisterKind.Head)
                return head[operand.Index] ?? throw new InvalidOperationException($"Head register {operand.Index} is used before its action.");

            return Entry(operand, variables, constants);
        }

        /// <summary>
        /// Gets the column types of operands.
        /// </summary>
        static ColumnType[] Types(Operand[] operands)
        {
            var types = new ColumnType[operands.Length];
            for (var i = 0; i < types.Length; i++)
                types[i] = operands[i].Type;

            return types;
        }

        /// <summary>
        /// Gets the source names of operands, for failure bindings: a register's name, or a constant's literal.
        /// </summary>
        static string[] Names(RuleProgram program, Operand[] operands)
        {
            var names = new string[operands.Length];
            for (var i = 0; i < names.Length; i++)
            {
                var operand = operands[i];
                names[i] = operand.Kind switch
                {
                    OperandKind.Register => program.Registers[operand.Index].Name,
                    OperandKind.Constant => operand.Type.FormatCell(operand.Bits),
                    _ => "#" + program.WrittenIdConstants[operand.Index],
                };
            }

            return names;
        }

    }

}
