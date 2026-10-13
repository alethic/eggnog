using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A random program of the rule differentials: its family, rules, conjunctive queries and commands, generated from
    /// a seed before anything runs, so that a failing program can be shrunk by dropping commands
    /// (<see cref="ColorShrinker"/>) and printed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Commands write and run rules in contexts up to three deep (four levels with the root), create children of
    /// writable and frozen contexts, change ancestors after their children exist, freeze contexts whose parents are
    /// frozen (the root at a random step, in two programs of three), dispose leaves, and in the history family delete
    /// and clear rows anywhere: the generator does not ask whether the engine's retraction rule allows it, the harness
    /// does, and checks the engine's refusal where it does not. Family A starts from a pool of root ids that rule
    /// constants name; family B from the terms <c>num(0..3)</c>.
    /// </para>
    /// <para>
    /// The generator keeps a structural model (which contexts live, are frozen, how deep, which ids each may use) so
    /// that generated commands are valid; the harness re-checks validity when it runs them, for shrunk programs.
    /// </para>
    /// </remarks>
    sealed class ColorProgram
    {

        /// <summary>
        /// The number of root ids family A mints before the commands, which rule constants may name.
        /// </summary>
        public const int PoolSize = 6;

        /// <summary>
        /// The most contexts alive at once.
        /// </summary>
        const int MaxLive = 7;

        /// <summary>
        /// Initializes a program.
        /// </summary>
        public ColorProgram(int seed, ColorFamily family, List<ColorRule> rules, List<ColorRule> queries, List<ColorCommand> commands)
        {
            Seed = seed;
            Family = family;
            Rules = rules;
            Queries = queries;
            Commands = commands;
        }

        /// <summary>
        /// Gets the seed it was generated from.
        /// </summary>
        public int Seed { get; }

        /// <summary>
        /// Gets the family.
        /// </summary>
        public ColorFamily Family { get; }

        /// <summary>
        /// Gets the rules; their id constants are pool indexes.
        /// </summary>
        public List<ColorRule> Rules { get; }

        /// <summary>
        /// Gets the conjunctive queries.
        /// </summary>
        public List<ColorRule> Queries { get; }

        /// <summary>
        /// Gets the commands.
        /// </summary>
        public List<ColorCommand> Commands { get; }

        /// <summary>
        /// Gets the mask of every rule.
        /// </summary>
        public int AllRules => (1 << Rules.Count) - 1;

        /// <summary>
        /// Returns a copy with <paramref name="commands"/> instead of the commands.
        /// </summary>
        public ColorProgram With(List<ColorCommand> commands) => new(Seed, Family, Rules, Queries, commands);

        /// <summary>
        /// Generates a program of <paramref name="steps"/> steps.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <param name="family">The family.</param>
        /// <param name="steps">The number of steps; children, freezes and disposals count as steps.</param>
        public static ColorProgram Generate(int seed, ColorFamily family, int steps)
        {
            var random = new Random(seed);
            var rules = new List<ColorRule>();
            var count = 3 + random.Next(3);
            for (var i = 0; i < count; i++)
                rules.Add(ColorRuleGenerator.Rule(random, family, "r" + i, PoolSize));

            var queries = new List<ColorRule>();
            for (var i = 0; i < 3; i++)
                queries.Add(ColorRuleGenerator.Query(random, family, "q" + i, PoolSize));

            // Tables deletes and clears may touch.
            var written = rules.SelectMany(r => r.WrittenTables()).ToHashSet();
            var retractable = Enumerable.Range(0, ColorSchema.Answer)
                .Where(t => family.Minting == false || (t != ColorSchema.Num && t != ColorSchema.Add && t != ColorSchema.Wrap))
                .Where(t => family.DeletesOnlyUnwritten == false || written.Contains(t) == false)
                .ToArray();

            // The structural model: per context slot, its parent, depth, and state; per id slot, its owner.
            var parent = new List<int> { -1 };
            var depth = new List<int> { 0 };
            var frozen = new List<bool> { false };
            var live = new List<bool> { true };
            var owner = new List<int>();
            var commands = new List<ColorCommand>();

            bool IsAncestorOrSelf(int a, int x)
            {
                for (var c = x; c >= 0; c = parent[c])
                    if (c == a)
                        return true;

                return false;
            }

            List<int> Ids(int x) => Enumerable.Range(0, owner.Count).Where(i => live[owner[i]] && IsAncestorOrSelf(owner[i], x)).ToList();
            int LiveChildren(int x) => Enumerable.Range(0, parent.Count).Count(c => parent[c] == x && live[c]);

            void Add(ColorCommand command) => commands.Add(command);

            int NewIdSlot(int x)
            {
                owner.Add(x);
                return owner.Count - 1;
            }

            if (family.Minting)
            {
                for (var v = 0; v < 4; v++)
                    Add(new ColorCommand(ColorCommandKind.Term, 0, ColorSchema.Num, [v], 0, NewIdSlot(0)));
            }
            else
            {
                for (var i = 0; i < PoolSize; i++)
                    owner.Add(0);
            }

            var freezeRootAt = random.Next(3) > 0 ? random.Next(steps / 8, steps / 2) : -1;
            for (var step = 0; step < steps; step++)
            {
                if (step == freezeRootAt && frozen[0] == false)
                {
                    Add(new ColorCommand(ColorCommandKind.Freeze, 0, -1, [], 0, -1));
                    frozen[0] = true;
                    continue;
                }

                var writable = Enumerable.Range(0, parent.Count).Where(c => live[c] && frozen[c] == false).ToList();
                if (writable.Count == 0)
                    break;

                var x = writable[random.Next(writable.Count)];
                var ids = Ids(x);

                // Ids are drawn mostly from a few, so rows join and rules fire.
                int PickId() => ids[random.Next(4) > 0 ? random.Next(Math.Min(ids.Count, 5)) : random.Next(ids.Count)];

                var roll = random.Next(100);
                if (ids.Count < 2 || roll < 6)
                {
                    if (family.Minting)
                    {
                        var term = random.Next(3);
                        if (term == 0 || ids.Count == 0)
                            Add(new ColorCommand(ColorCommandKind.Term, x, ColorSchema.Num, [random.Next(6)], 0, NewIdSlot(x)));
                        else if (term == 1)
                            Add(new ColorCommand(ColorCommandKind.Term, x, ColorSchema.Add, [PickId(), PickId()], 0, NewIdSlot(x)));
                        else
                            Add(new ColorCommand(ColorCommandKind.Term, x, ColorSchema.Wrap, [PickId()], 0, NewIdSlot(x)));
                    }
                    else
                    {
                        Add(new ColorCommand(ColorCommandKind.NewId, x, -1, [], 0, NewIdSlot(x)));
                    }

                    continue;
                }

                switch (roll)
                {
                    case < 36:
                    {
                        int[] tables = family.Minting
                            ? [ColorSchema.Edge, ColorSchema.Edge, ColorSchema.Path, ColorSchema.Mark, ColorSchema.Low]
                            : [ColorSchema.Edge, ColorSchema.Edge, ColorSchema.Path, ColorSchema.Mark, ColorSchema.Low, ColorSchema.Num, ColorSchema.Add, ColorSchema.Wrap];
                        var table = tables[random.Next(tables.Length)];
                        var isId = ColorSchema.IdColumns[table];
                        var cells = new int[isId.Length];
                        for (var c = 0; c < cells.Length; c++)
                            cells[c] = isId[c] ? PickId() : random.Next(table == ColorSchema.Num ? 6 : 10);

                        Add(new ColorCommand(ColorCommandKind.Row, x, table, cells, 0, -1));
                        break;
                    }
                    case < 44:
                        Add(new ColorCommand(ColorCommandKind.Union, x, -1, [PickId(), PickId()], 0, -1));
                        break;
                    case < 56:
                        Add(new ColorCommand(ColorCommandKind.Run, x, -1, [], 1 + random.Next((1 << rules.Count) - 1), -1));
                        break;
                    case < 63:
                        Add(new ColorCommand(ColorCommandKind.Saturate, x, -1, [], random.Next(3) == 0 ? 1 + random.Next((1 << rules.Count) - 1) : (1 << rules.Count) - 1, random.Next(2)));
                        break;
                    case < 73:
                    {
                        // A child of a writable or a frozen context.
                        var candidates = Enumerable.Range(0, parent.Count).Where(c => live[c] && depth[c] < 3).ToList();
                        if (candidates.Count == 0 || live.Count(l => l) >= MaxLive)
                            break;

                        var p = candidates[random.Next(candidates.Count)];
                        parent.Add(p);
                        depth.Add(depth[p] + 1);
                        frozen.Add(false);
                        live.Add(true);
                        Add(new ColorCommand(ColorCommandKind.Child, p, -1, [], 0, parent.Count - 1));
                        break;
                    }
                    case < 79:
                        if (x != 0 && frozen[parent[x]])
                        {
                            Add(new ColorCommand(ColorCommandKind.Freeze, x, -1, [], 0, -1));
                            frozen[x] = true;
                        }

                        break;
                    case < 88:
                        if (family.History && retractable.Length > 0)
                            Add(new ColorCommand(ColorCommandKind.Delete, x, retractable[random.Next(retractable.Length)], [], random.Next(1000), -1));

                        break;
                    case < 89:
                        if (family.History && retractable.Length > 0)
                            Add(new ColorCommand(ColorCommandKind.Clear, x, retractable[random.Next(retractable.Length)], [], 0, -1));

                        break;
                    case < 93:
                    {
                        // Dispose a leaf (writable or frozen).
                        var leaves = Enumerable.Range(1, parent.Count - 1).Where(c => live[c] && LiveChildren(c) == 0).ToList();
                        if (leaves.Count == 0)
                            break;

                        var leaf = leaves[random.Next(leaves.Count)];
                        live[leaf] = false;
                        Add(new ColorCommand(ColorCommandKind.Dispose, leaf, -1, [], 0, -1));
                        break;
                    }
                    case < 96:
                        Add(new ColorCommand(ColorCommandKind.Rebuild, x, -1, [], 0, -1));
                        break;
                    default:
                        Add(new ColorCommand(ColorCommandKind.Check, x, -1, [], 0, -1));
                        break;
                }
            }

            return new ColorProgram(seed, family, rules, queries, commands);
        }

        /// <summary>
        /// Prints the program: seed, family, rules, queries and commands.
        /// </summary>
        public string Describe()
        {
            var b = new StringBuilder();
            b.AppendLine($"seed {Seed}, {Family}");
            foreach (var rule in Rules)
                b.AppendLine("  " + rule.Describe());

            foreach (var query in Queries)
                b.AppendLine("  " + query.Describe());

            for (var i = 0; i < Commands.Count; i++)
                b.AppendLine($"  {i,3} {Commands[i].Describe()}");

            return b.ToString();
        }

    }

}
