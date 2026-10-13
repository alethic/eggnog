using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// Generates a <see cref="ScopedProgram"/> from a seed. Values come from small domains (<c>i64</c> 0 to 3, strings
    /// a to c), so programs stay small; every rule is valid: head variables are bound by the body, and a local rule's
    /// scoped atoms read only tags that resolve from its owner.
    /// </summary>
    internal sealed class ScopedProgramGenerator
    {

        static readonly string[] Strings = ["a", "b", "c"];

        readonly int seed;
        readonly bool constructors;
        readonly Random random;
        readonly List<ScopedTableSpec> tables = new();
        readonly List<(int Parent, int Depth, int Tag, bool Frozen)> scopes = new();
        int rules;

        /// <summary>
        /// Initializes a generator.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <param name="constructors">Whether programs have constructors in children.</param>
        public ScopedProgramGenerator(int seed, bool constructors)
        {
            this.seed = seed;
            this.constructors = constructors;
            random = new Random(seed);
        }

        /// <summary>
        /// Generates the program.
        /// </summary>
        /// <returns>The program.</returns>
        public ScopedProgram Generate()
        {
            var relations = random.Next(2, 4);
            for (var i = 0; i < relations; i++)
            {
                var arity = random.Next(1, 3);
                tables.Add(new ScopedTableSpec("R" + i.ToString(CultureInfo.InvariantCulture), ScopedTableKind.Relation,
                    Enumerable.Range(0, arity).Select(_ => random.Next(3) == 0).ToArray(), false));
            }

            var functions = random.Next(1, 3);
            for (var i = 0; i < functions; i++)
                tables.Add(new ScopedTableSpec("F" + i.ToString(CultureInfo.InvariantCulture), ScopedTableKind.Function, [random.Next(3) == 0], random.Next(2) == 0));

            if (constructors)
            {
                tables.Add(new ScopedTableSpec("C", ScopedTableKind.Constructor, [false], false));
                tables.Add(new ScopedTableSpec("N", ScopedTableKind.NodeRelation, [false], false));
            }

            var globals = new List<ScopedRuleSpec>();
            var globalCount = random.Next(2, 5);
            for (var i = 0; i < globalCount; i++)
                globals.Add(Rule(Array.Empty<int>()));

            var queries = new List<ScopedRuleSpec>();
            var queryCount = random.Next(2, 4);
            for (var i = 0; i < queryCount; i++)
                queries.Add(Query(i));

            scopes.Add((-1, 0, -1, false));
            var commands = new List<ScopedCommand>();
            var steps = random.Next(10, 17);
            for (var step = 0; step < steps; step++)
            {
                var writable = Enumerable.Range(0, scopes.Count).Where(s => scopes[s].Frozen == false).ToArray();
                switch (random.Next(10))
                {
                    case 0:
                    case 1:
                    case 2:
                        if (scopes.Count < 6 && Create() is { } create)
                            commands.Add(create);

                        break;
                    case 3:
                    case 4:
                    case 5:
                        if (writable.Length > 0)
                            commands.Add(Assume(writable[random.Next(writable.Length)]));

                        break;
                    case 6:
                    case 7:
                        if (writable.Length > 0)
                            commands.Add(ScopedCommand.Of(ScopedCommandKind.Run, writable[random.Next(writable.Length)]));

                        break;
                    case 8:
                    {
                        var freezable = writable.Where(s => scopes[s].Parent < 0 || scopes[scopes[s].Parent].Frozen).ToArray();
                        if (freezable.Length > 0)
                        {
                            var scope = freezable[random.Next(freezable.Length)];
                            scopes[scope] = (scopes[scope].Parent, scopes[scope].Depth, scopes[scope].Tag, true);
                            commands.Add(ScopedCommand.Of(ScopedCommandKind.Freeze, scope));
                        }

                        break;
                    }
                    default:
                        commands.Add(ScopedCommand.Of(ScopedCommandKind.Check, 0));
                        break;
                }
            }

            commands.Add(ScopedCommand.Of(ScopedCommandKind.Check, 0));
            return new ScopedProgram(seed, tables.ToArray(), globals.ToArray(), queries.ToArray(), commands.ToArray(), constructors);
        }

        /// <summary>
        /// A new scope under an existing one less than four deep, with a tag and up to two local rules.
        /// </summary>
        ScopedCommand? Create()
        {
            var parents = Enumerable.Range(0, scopes.Count).Where(s => scopes[s].Depth < 4).ToArray();
            if (parents.Length == 0)
                return null;

            var parent = parents[random.Next(parents.Length)];
            var tag = random.Next(3) - 1;
            var index = scopes.Count;
            scopes.Add((parent, scopes[parent].Depth + 1, tag, false));
            var resolvable = new HashSet<int>();
            for (var s = index; s >= 0; s = scopes[s].Parent)
                if (scopes[s].Tag >= 0)
                    resolvable.Add(scopes[s].Tag);

            var locals = new List<ScopedRuleSpec>();
            var count = random.Next(3);
            for (var i = 0; i < count; i++)
                locals.Add(Rule(resolvable.ToArray()));

            return new ScopedCommand(ScopedCommandKind.Create, index, parent, tag, Array.Empty<ScopedAtomSpec>(), locals.ToArray());
        }

        /// <summary>
        /// One to three ground facts for a writable scope.
        /// </summary>
        ScopedCommand Assume(int scope)
        {
            var facts = new List<ScopedAtomSpec>();
            var count = random.Next(1, 4);
            for (var i = 0; i < count; i++)
            {
                var table = WritableTable();
                var spec = tables[table];
                if (spec.Kind == ScopedTableKind.NodeRelation)
                {
                    facts.Add(new ScopedAtomSpec(table, [new ScopedTermSpec(null, null, ConstructorIndex(), Constant(false))], null, -1));
                    continue;
                }

                var keys = spec.StringKeys.Select(Constant).ToArray();
                facts.Add(new ScopedAtomSpec(table, keys, spec.Kind == ScopedTableKind.Function ? Constant(false) : null, -1));
            }

            return new ScopedCommand(ScopedCommandKind.Assume, scope, -1, -1, facts.ToArray(), Array.Empty<ScopedRuleSpec>());
        }

        /// <summary>
        /// A rule whose scoped atoms read only <paramref name="tags"/>.
        /// </summary>
        ScopedRuleSpec Rule(int[] tags)
        {
            var name = "r" + (rules++).ToString(CultureInfo.InvariantCulture);
            var bound = new List<string>();
            var body = new List<ScopedAtomSpec>();
            var atoms = random.Next(1, 4);
            for (var i = 0; i < atoms; i++)
                AddBodyAtom(body, bound, () => BodyAtom(bound, tags));

            string? filter = null;
            var longs = bound.Where(v => v[0] == 'x').ToArray();
            if (longs.Length > 0 && random.Next(3) == 0)
                filter = longs[random.Next(longs.Length)];

            var head = new List<ScopedAtomSpec>();
            var heads = random.Next(1, 3);
            for (var i = 0; i < heads; i++)
                head.Add(HeadAtom(bound));

            return new ScopedRuleSpec(name, body.ToArray(), filter, random.Next(1, 4), head.ToArray());
        }

        /// <summary>
        /// A query of one or two atoms, possibly scoped to either tag.
        /// </summary>
        ScopedRuleSpec Query(int index)
        {
            var bound = new List<string>();
            var body = new List<ScopedAtomSpec>();
            var atoms = random.Next(1, 3);
            for (var i = 0; i < atoms; i++)
                AddBodyAtom(body, bound, () => BodyAtom(bound, [0, 1], valueOnly: true));

            return new ScopedRuleSpec("q" + index.ToString(CultureInfo.InvariantCulture), body.ToArray(), null, 0, Array.Empty<ScopedAtomSpec>());
        }

        /// <summary>
        /// Adds the body atom <paramref name="make"/> makes, unless it applies a function or constructor already
        /// applied to the same keys in the same scope (the rule compiler gives equal applications one output, so a
        /// second one would bind nothing new); the variables a refused atom introduced are dropped.
        /// </summary>
        static void AddBodyAtom(List<ScopedAtomSpec> body, List<string> bound, Func<ScopedAtomSpec> make)
        {
            var before = bound.Count;
            var atom = make();
            if (atom.Output is not null && body.Any(a => a.Table == atom.Table && a.Tag == atom.Tag && a.Keys.SequenceEqual(atom.Keys)))
            {
                bound.RemoveRange(before, bound.Count - before);
                return;
            }

            body.Add(atom);
        }

        /// <summary>
        /// A body atom: a value table's row or value, scoped to one of <paramref name="tags"/> at times; in programs
        /// with constructors, also a constructor's value or a node relation's row.
        /// </summary>
        ScopedAtomSpec BodyAtom(List<string> bound, int[] tags, bool valueOnly = false)
        {
            var candidates = Enumerable.Range(0, tables.Count).Where(t => valueOnly == false || tables[t].IsValueOnly).ToArray();
            var table = candidates[random.Next(candidates.Length)];
            var spec = tables[table];
            switch (spec.Kind)
            {
                case ScopedTableKind.Constructor:
                    return new ScopedAtomSpec(table, [BodyTerm(bound, false)], Var(bound, 'n'), -1);
                case ScopedTableKind.NodeRelation:
                    return new ScopedAtomSpec(table, [Var(bound, 'n')], null, -1);
            }

            var tag = tags.Length > 0 && random.Next(5) < 2 ? tags[random.Next(tags.Length)] : -1;
            var keys = spec.StringKeys.Select(s => BodyTerm(bound, s)).ToArray();
            // A function's value is read into a fresh variable only (u for a max function, w for a min one), which
            // heads write only into functions merged the same way: a value read before it settled then never matters,
            // because the final value bounds every earlier one. Joining, filtering or recording such a value would
            // depend on when rules ran, which differs between scopes and the literal encoding (and between seeded and
            // unseeded runs).
            ScopedTermSpec? output = null;
            if (spec.Kind == ScopedTableKind.Function)
            {
                var name = (spec.Max ? "u" : "w") + bound.Count.ToString(CultureInfo.InvariantCulture);
                bound.Add(name);
                output = ScopedTermSpec.Variable(name);
            }

            return new ScopedAtomSpec(table, keys, output, tag);
        }

        /// <summary>
        /// A body term: a constant at times, else a variable, reused when one of the type is bound.
        /// </summary>
        ScopedTermSpec BodyTerm(List<string> bound, bool isString) =>
            random.Next(4) == 0 ? Constant(isString) : Var(bound, isString ? 's' : 'x');

        /// <summary>
        /// A variable of a type: a bound one more often than not, else a new one.
        /// </summary>
        ScopedTermSpec Var(List<string> bound, char type)
        {
            var existing = bound.Where(v => v[0] == type).ToArray();
            if (existing.Length > 0 && random.Next(3) > 0)
                return ScopedTermSpec.Variable(existing[random.Next(existing.Length)]);

            var name = type + bound.Count.ToString(CultureInfo.InvariantCulture);
            bound.Add(name);
            return ScopedTermSpec.Variable(name);
        }

        /// <summary>
        /// A head atom writing a value table (or, with constructors, a node row of a made term) from bound variables
        /// and constants.
        /// </summary>
        ScopedAtomSpec HeadAtom(List<string> bound)
        {
            var table = WritableTable();
            var spec = tables[table];
            if (spec.Kind == ScopedTableKind.NodeRelation)
            {
                var nodes = bound.Where(v => v[0] == 'n').ToArray();
                var term = nodes.Length > 0 && random.Next(2) == 0
                    ? ScopedTermSpec.Variable(nodes[random.Next(nodes.Length)])
                    : new ScopedTermSpec(null, null, ConstructorIndex(), HeadTerm(bound, false));
                return new ScopedAtomSpec(table, [term], null, -1);
            }

            var keys = spec.StringKeys.Select(s => HeadTerm(bound, s)).ToArray();
            if (spec.Kind != ScopedTableKind.Function)
                return new ScopedAtomSpec(table, keys, null, -1);

            var values = bound.Where(v => v[0] == 'x' || v[0] == (spec.Max ? 'u' : 'w')).ToArray();
            var value = values.Length > 0 && random.Next(4) > 0 ? ScopedTermSpec.Variable(values[random.Next(values.Length)]) : Constant(false);
            return new ScopedAtomSpec(table, keys, value, -1);
        }

        /// <summary>
        /// A head term: a bound variable of the type, or a constant.
        /// </summary>
        ScopedTermSpec HeadTerm(List<string> bound, bool isString)
        {
            var existing = bound.Where(v => v[0] == (isString ? 's' : 'x')).ToArray();
            return existing.Length > 0 && random.Next(4) > 0 ? ScopedTermSpec.Variable(existing[random.Next(existing.Length)]) : Constant(isString);
        }

        /// <summary>
        /// A constant of the domain.
        /// </summary>
        ScopedTermSpec Constant(bool isString) => ScopedTermSpec.Value(isString ? Strings[random.Next(Strings.Length)] : (object)(long)random.Next(4));

        /// <summary>
        /// A table a head or a host write may write: a relation, a function, or with constructors the node relation.
        /// </summary>
        int WritableTable()
        {
            var candidates = Enumerable.Range(0, tables.Count).Where(t => tables[t].Kind != ScopedTableKind.Constructor).ToArray();
            return candidates[random.Next(candidates.Length)];
        }

        /// <summary>
        /// The constructor's table index.
        /// </summary>
        int ConstructorIndex() => tables.FindIndex(t => t.Kind == ScopedTableKind.Constructor);

    }

}
