using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// The oracle of <c>ScopeDifferentialTests</c>: a value-only <see cref="ScopedProgram"/> translated into the
    /// literal encoding and run at one root. Every table gains a leading context key and every scope becomes a context
    /// constant; inheritance rules <c>parent(k, c), T(c, x…) → T(k, x…)</c> copy a parent's rows into each child; and
    /// every rule active in a scope is instantiated for it, its scoped atoms resolved statically to the tagged scope's
    /// constant. A check saturates, then renders each scope's rows as <see cref="ScopedDriver"/> does.
    /// </summary>
    internal sealed class LiteralOracle
    {

        readonly ScopedProgram program;
        readonly EGraph graph;
        readonly ColumnType integer;
        readonly ColumnType text;
        readonly TableHandle[] tables;
        readonly RelationHandle parent;
        readonly Ruleset rules;
        readonly List<int> parents = new() { -1 };
        readonly List<int> scopeTags = new() { -1 };
        readonly List<ScopedRuleSpec[]> locals = new() { Array.Empty<ScopedRuleSpec>() };
        int instances;

        /// <summary>
        /// Defines the encoded tables, the inheritance rules, and the root's instances of the global rules.
        /// </summary>
        /// <param name="program">The program, which has no constructors.</param>
        public LiteralOracle(ScopedProgram program)
        {
            if (program.Constructors)
                throw new ArgumentException("The literal oracle encodes value-only programs.", nameof(program));

            this.program = program;
            graph = new EGraph();
            integer = graph.Values.TypeOf<long>();
            text = graph.Values.TypeOf<string>();
            tables = new TableHandle[program.Tables.Length];
            for (var i = 0; i < tables.Length; i++)
            {
                var spec = program.Tables[i];
                var keys = new[] { integer }.Concat(spec.StringKeys.Select(s => s ? text : integer)).ToArray();
                var name = "T" + i.ToString(CultureInfo.InvariantCulture);
                tables[i] = spec.Kind == ScopedTableKind.Relation
                    ? graph.DefineRelation(name, keys)
                    : graph.DefineFunction(name, keys, integer, spec.Max ? ValueLattice.Max<long>() : ValueLattice.Min<long>());
            }

            parent = graph.DefineRelation("parent", [integer, integer]);
            rules = graph.DefineRuleset("all");
            for (var i = 0; i < tables.Length; i++)
            {
                var table = i;
                rules.DefineRule("inherit T" + i.ToString(CultureInfo.InvariantCulture), b =>
                {
                    var (k, c) = (b.Var(integer, "k"), b.Var(integer, "c"));
                    var spec = program.Tables[table];
                    var keys = spec.StringKeys.Select((s, n) => b.Var(s ? text : integer, "x" + n.ToString(CultureInfo.InvariantCulture))).ToArray();
                    b.When(parent.Apply(k, c));
                    if (spec.Kind == ScopedTableKind.Relation)
                    {
                        b.When(((RelationHandle)tables[table]).Apply([c, .. keys]));
                        b.Then(((RelationHandle)tables[table]).Apply([k, .. keys]));
                    }
                    else
                    {
                        var v = b.Var(integer, "v");
                        b.When(Fact.Equal(((FunctionHandle)tables[table]).Apply([c, .. keys]), v));
                        b.Then(Fact.Equal(((FunctionHandle)tables[table]).Apply([k, .. keys]), v));
                    }
                });
            }

            Instantiate(0);
        }

        /// <summary>
        /// Runs a command; a check returns every scope's rendering, by number.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <returns>The renderings of a check; <c>null</c> otherwise.</returns>
        public Dictionary<int, string>? Execute(ScopedCommand command)
        {
            switch (command.Kind)
            {
                case ScopedCommandKind.Create:
                    parents.Add(command.Parent);
                    scopeTags.Add(command.Tag);
                    locals.Add(command.Rules);
                    graph.Root.Assume(parent.Apply(Constant(command.Scope), Constant(command.Parent)));
                    Instantiate(command.Scope);
                    return null;
                case ScopedCommandKind.Assume:
                    graph.Root.Assume(command.Facts.Select(f => Atom(f, command.Scope, null)).ToArray());
                    return null;
                case ScopedCommandKind.Check:
                {
                    var report = graph.Root.Run(Schedule.Saturate(rules), new RunOptions { IterationLimit = 1000 });
                    if (report.StopReason != RunStopReason.Completed)
                        throw new InvalidOperationException($"The oracle did not saturate: {report.StopReason}.");

                    var renderings = new Dictionary<int, string>();
                    for (var i = 0; i < parents.Count; i++)
                        renderings[i] = Render(i);

                    return renderings;
                }
                default:
                    return null;
            }
        }

        /// <summary>
        /// Gets the nearest ancestor-or-self of a scope tagged <paramref name="tag"/>, or -1.
        /// </summary>
        int Resolve(int scope, int tag)
        {
            for (var s = scope; s >= 0; s = parents[s])
                if (scopeTags[s] == tag)
                    return s;

            return -1;
        }

        /// <summary>
        /// Defines, for a new scope, an instance of every rule active in it: the global rules, then the local rules of
        /// the scope and its ancestors.
        /// </summary>
        void Instantiate(int scope)
        {
            var active = new List<ScopedRuleSpec>(program.Globals);
            var path = new List<int>();
            for (var s = scope; s >= 0; s = parents[s])
                path.Add(s);

            for (var k = path.Count - 1; k >= 0; k--)
                active.AddRange(locals[path[k]]);

            foreach (var rule in active)
            {
                var name = rule.Name + " in s" + scope.ToString(CultureInfo.InvariantCulture) + " #" + (instances++).ToString(CultureInfo.InvariantCulture);
                rules.DefineRule(name, b =>
                {
                    var variables = new Dictionary<string, Term>(StringComparer.Ordinal);
                    foreach (var variable in rule.Variables)
                        variables[variable] = b.Var(variable[0] == 's' ? text : integer, variable);

                    foreach (var atom in rule.Body)
                        b.When(Atom(atom, atom.Tag >= 0 ? Resolve(scope, atom.Tag) : scope, variables));

                    if (rule.FilterVar is { } filtered)
                        b.When(new FilterFact(Compute.Of(Primitives.Less, variables[filtered], Term.Constant(integer, rule.FilterBelow))));

                    foreach (var atom in rule.Head)
                        b.Then(Atom(atom, scope, variables));
                });
            }
        }

        /// <summary>
        /// Renders a scope as <see cref="ScopedDriver"/> renders it without names: its value tables, its scoped reads,
        /// and its queries' answers.
        /// </summary>
        string Render(int scope)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < tables.Length; i++)
            {
                builder.Append(Rows(i, scope)).Append('\n');
                for (var j = 0; j < ScopedProgram.TagCount; j++)
                {
                    var tagged = Resolve(scope, j);
                    if (tagged >= 0)
                        builder.Append(tables[i].Name).Append("@t").Append(j).Append(' ').Append(Rows(i, tagged)).Append('\n');
                }
            }

            foreach (var query in program.Queries)
            {
                if (query.Tags.Any(t => Resolve(scope, t) < 0))
                    continue;

                var variables = query.Variables;
                var name = query.Name + " in s" + scope.ToString(CultureInfo.InvariantCulture) + " #" + (instances++).ToString(CultureInfo.InvariantCulture);
                using var instance = graph.DefineQuery(name, q =>
                {
                    var terms = new Dictionary<string, Term>(StringComparer.Ordinal);
                    foreach (var variable in variables)
                        terms[variable] = q.Var(variable[0] == 's' ? text : integer, variable);

                    foreach (var atom in query.Body)
                        q.When(Atom(atom, atom.Tag >= 0 ? Resolve(scope, atom.Tag) : scope, terms));
                });
                var answers = new List<string>();
                graph.Root.ForEachMatch(instance, m =>
                {
                    var parts = new string[instance.Variables.Count];
                    for (var v = 0; v < parts.Length; v++)
                        parts[v] = ScopedDriver.Format(m.Get(instance.Variables[v]));

                    answers.Add(string.Join(" ", parts));
                    return true;
                });
                answers.Sort(StringComparer.Ordinal);
                builder.Append(query.Name).Append(" count=").Append(answers.Count).Append(": ").AppendJoin(" | ", answers).Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Renders one table's rows in one scope, the context key dropped.
        /// </summary>
        string Rows(int table, int scope)
        {
            var name = tables[table].Name;
            var rows = graph.Root.Rows(tables[table]).Where(r => (long)r.Keys[0] == scope)
                .Select(r => ScopedDriver.Row(name, r.Keys.Skip(1), r.Output))
                .OrderBy(r => r, StringComparer.Ordinal);
            return name + ": " + string.Join(" | ", rows);
        }

        /// <summary>
        /// Builds an atom's fact in the encoding: the context constant first.
        /// </summary>
        Fact Atom(ScopedAtomSpec atom, int scope, Dictionary<string, Term>? variables)
        {
            var keys = new[] { Constant(scope) }.Concat(atom.Keys.Select(k => TermOf(k, variables))).ToArray();
            return program.Tables[atom.Table].Kind == ScopedTableKind.Relation
                ? ((RelationHandle)tables[atom.Table]).Apply(keys)
                : Fact.Equal(((FunctionHandle)tables[atom.Table]).Apply(keys), TermOf(atom.Output!, variables));
        }

        /// <summary>
        /// Builds a term.
        /// </summary>
        Term TermOf(ScopedTermSpec term, Dictionary<string, Term>? variables) =>
            term.Var is { } name ? variables![name] : Eggnog.Term.Constant(term.Constant is string ? text : integer, term.Constant!);

        /// <summary>
        /// A context constant.
        /// </summary>
        Term Constant(int scope) => Eggnog.Term.Constant(integer, (long)scope);

    }

}
