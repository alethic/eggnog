using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// Prints a <see cref="RandomProgram"/> as C# source against the public API (typed handles, <c>Primitives</c>,
    /// lambdas), so that a failing seed can be read, and pasted into a test, as a program.
    /// </summary>
    internal static class ProgramPrinter
    {

        /// <summary>
        /// Prints a program.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <returns>The source.</returns>
        public static string Print(RandomProgram program)
        {
            var builder = new StringBuilder();
            builder.Append("// Random program, seed ").Append(program.Seed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("var graph = new EGraph();\n");
            builder.Append("graph.DefineSort<Node>(\"Node\");\n");
            foreach (var table in program.Tables)
                builder.Append(Definition(table)).Append('\n');

            for (var i = 0; i < program.Rulesets; i++)
                builder.Append($"var rs{i} = graph.DefineRuleset(\"rs{i}\");\n");

            foreach (var query in program.Queries)
                builder.Append(Query(query));

            builder.Append("var root = graph.Root;\n");
            builder.Append("var handles = new List<Node>();\n");
            foreach (var command in program.Commands)
                builder.Append(Command(command));

            return builder.ToString();
        }

        /// <summary>
        /// Prints a term.
        /// </summary>
        /// <param name="term">The term.</param>
        /// <returns>The C# expression.</returns>
        public static string Term(DiffTerm term) => term.Kind switch
        {
            DiffTermKind.Var => term.Name!,
            DiffTermKind.Const => Constant(term),
            DiffTermKind.App => $"{term.Table!.Name}.Of({string.Join(", ", term.Arguments.Select(Term))})",
            _ => term.Primitive!.CSharp(term.Arguments.Select(Term).ToArray()),
        };

        /// <summary>
        /// Prints a fact.
        /// </summary>
        /// <param name="fact">The fact.</param>
        /// <returns>The C# expression.</returns>
        public static string Fact(DiffFact fact) => fact.Kind switch
        {
            DiffFactKind.Relation => $"{fact.Table!.Name}.Of({string.Join(", ", fact.Arguments.Select(Term))})",
            DiffFactKind.Equal => $"{Receiver(fact.Left!)}.Is({Term(fact.Right!)})",
            DiffFactKind.NotEqual => $"{Receiver(fact.Left!)}.IsNot({Term(fact.Right!)})",
            DiffFactKind.Filter => $"Fact.True({Term(fact.Left!)})",
            _ => $"Fact.Exists({Term(fact.Left!)})",
        };

        /// <summary>
        /// Prints a table's definition.
        /// </summary>
        static string Definition(DiffTable table)
        {
            var keys = string.Join(", ", table.Keys.Select(Type));
            return table.Kind switch
            {
                TableKind.Relation => $"var {table.Name} = graph.DefineRelation<{keys}>(\"{table.Name}\");",
                TableKind.Constructor => $"var {table.Name} = graph.DefineConstructor<{keys}, Node>(\"{table.Name}\");",
                _ => $"var {table.Name} = graph.DefineFunction<{keys}, {Type(table.Output!.Value)}>(\"{table.Name}\"{table.Lattice switch
                {
                    DiffLattice.Max => ", ValueLattice.Max<long>()",
                    DiffLattice.Intersect => ", ValueLattice.Intersect<int>()",
                    _ => "",
                }});",
            };
        }

        /// <summary>
        /// Prints a command.
        /// </summary>
        static string Command(DiffCommand command)
        {
            switch (command.Kind)
            {
                case DiffCommandKind.Assume:
                    return $"root.Assume({string.Join(", ", command.Facts.Select(Fact))});\n";
                case DiffCommandKind.Make:
                    return $"handles.Add(root.Make({Term(command.Term!)}));\n";
                case DiffCommandKind.Union:
                    return $"root.Union(handles[{command.First}], handles[{command.Second}]);\n";
                case DiffCommandKind.Run:
                    return $"root.Run({Schedule(command.Schedule!)}, new RunOptions {{ IterationLimit = {command.First} }});\n";
                case DiffCommandKind.DefineQuery:
                    return Query(command.Rule!);
                case DiffCommandKind.Query:
                    return command.Mode switch
                    {
                        DiffQueryMode.Any => $"root.Any(q{command.First});\n",
                        DiffQueryMode.Count => $"root.Count(q{command.First});\n",
                        _ => $"root.ForEachMatch(q{command.First}, m => true);\n",
                    };
                default:
                    return Rule(command.Rule!);
            }
        }

        /// <summary>
        /// Prints a query's definition.
        /// </summary>
        static string Query(DiffRule query)
        {
            var builder = new StringBuilder();
            builder.Append($"var {query.Name} = graph.DefineQuery(\"{query.Name}\", q =>\n{{\n");
            foreach (var (name, type) in query.Variables)
                builder.Append($"    var {name} = q.Var<{Type(type)}>(\"{name}\");\n");

            foreach (var fact in query.Body)
                builder.Append("    q.When(").Append(Fact(fact)).Append(");\n");

            return builder.Append("});\n").ToString();
        }

        /// <summary>
        /// Prints a rule's definition.
        /// </summary>
        static string Rule(DiffRule rule)
        {
            var builder = new StringBuilder();
            builder.Append($"rs{rule.Ruleset}.DefineRule(\"{rule.Name}\", r =>\n{{\n");
            foreach (var (name, type) in rule.Variables)
                builder.Append($"    var {name} = r.Var<{Type(type)}>(\"{name}\");\n");

            if (rule.Naive)
                builder.Append("    r.Naive();\n");

            foreach (var fact in rule.Body)
                builder.Append("    r.When(").Append(Fact(fact)).Append(");\n");

            foreach (var item in rule.Head)
            {
                switch (item.Kind)
                {
                    case DiffHeadKind.Then:
                        builder.Append("    r.Then(").Append(Fact(item.Fact!)).Append(");\n");
                        break;
                    case DiffHeadKind.Make:
                        builder.Append($"    var {item.Name} = r.Make(\"{item.Name}\", {Term(item.Left!)});\n");
                        break;
                    default:
                        builder.Append($"    r.Rewrite({Term(item.Left!)}, {Term(item.Right!)});\n");
                        break;
                }
            }

            return builder.Append("});\n").ToString();
        }

        /// <summary>
        /// Prints a schedule.
        /// </summary>
        static string Schedule(DiffSchedule schedule) => schedule.Kind switch
        {
            DiffScheduleKind.Run => $"Schedule.Run({string.Join(", ", schedule.Rulesets.Select(r => "rs" + r.ToString(CultureInfo.InvariantCulture)))})",
            DiffScheduleKind.Saturate => $"Schedule.Saturate({string.Join(", ", schedule.Steps.Select(Schedule))})",
            DiffScheduleKind.Repeat => $"Schedule.Repeat({schedule.Times}, {string.Join(", ", schedule.Steps.Select(Schedule))})",
            DiffScheduleKind.Sequence => $"Schedule.Sequence({string.Join(", ", schedule.Steps.Select(Schedule))})",
            _ => $"Schedule.Until({Schedule(schedule.Steps[0])}, {string.Join(", ", schedule.Condition.Select(Fact))})",
        };

        /// <summary>
        /// Prints the receiver of <c>Is</c>: a constant is cast to its term type.
        /// </summary>
        static string Receiver(DiffTerm term) => term.Kind == DiffTermKind.Const ? $"((Term<{Type(term.Type)}>){Constant(term)})" : Term(term);

        /// <summary>
        /// Prints a constant.
        /// </summary>
        static string Constant(DiffTerm term) => term.Type switch
        {
            DiffType.I64 => ((long)term.Value!).ToString(CultureInfo.InvariantCulture) + "L",
            DiffType.Str => "\"" + (string)term.Value! + "\"",
            DiffType.IntSet => ((ValueSet<int>)term.Value!).IsEmpty ? "ValueSet<int>.Empty" : $"ValueSet.Of({string.Join(", ", (ValueSet<int>)term.Value!)})",
            _ => $"handles[{term.Value}]",
        };

        /// <summary>
        /// Prints a type.
        /// </summary>
        static string Type(DiffType type) => type switch
        {
            DiffType.I64 => "long",
            DiffType.Str => "string",
            DiffType.IntSet => "ValueSet<int>",
            DiffType.Node => "Node",
            _ => "bool",
        };

    }

}
