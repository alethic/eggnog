using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Generates the random rules of the rule differentials, per program family (<see cref="ColorFamily"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bodies have one to three atoms; every atom after the first joins on an earlier variable, so matches stay few.
    /// Family A's id constants are indexes into the program's pool of root ids (<see cref="ColorRule.Resolve"/>
    /// replaces them with the ids when the program runs), and its heads write relations, the lattice <c>low</c>,
    /// constructor rows with an output variable, and unions. The history family adds <see cref="ColorFilter"/>s and
    /// <see cref="ColorActionKind.Read"/>s; the id-order filter only in family A, whose ids both sides share.
    /// </para>
    /// <para>
    /// Family B must terminate although rules mint. Its tables are split into the lower stratum <c>edge</c>,
    /// <c>mark</c>, <c>low</c>, <c>num</c> and the upper <c>path</c>, <c>add</c>, <c>wrap</c>. A rule that writes the
    /// lower stratum, or mints, reads only the lower stratum; a rule that reads the upper stratum writes only
    /// <c>path</c> or unions. So the lower stratum never holds a minted class that was not already one of its classes
    /// (unions only merge classes), the bindings minting rules see are finitely many, and so are the terms they mint.
    /// </para>
    /// <para>
    /// The monotone family's bodies never read a lattice output: a <c>low</c> atom binds its value to a variable of its
    /// own, which nothing else reads (<see cref="IsLatticeOutput"/>). A body that joins on <c>low</c>'s value is not
    /// monotone: once a write lowers it, facts derived from the old value persist (at a plain root too), while the
    /// fresh root of <see cref="ContextRuleCopyTests"/>, which replays every write before it saturates, never sees the
    /// old value. Such bodies stay in the history family, which the operational reference checks. Conjunctive queries
    /// (<see cref="Query"/>) may read <c>low</c>'s value: they run once over views that agree.
    /// </para>
    /// </remarks>
    static class ColorRuleGenerator
    {

        static readonly int[] AllTables = [ColorSchema.Edge, ColorSchema.Path, ColorSchema.Mark, ColorSchema.Low, ColorSchema.Num, ColorSchema.Add, ColorSchema.Wrap];

        static readonly int[] Lower = [ColorSchema.Edge, ColorSchema.Mark, ColorSchema.Low, ColorSchema.Num];

        /// <summary>
        /// Generates a program rule.
        /// </summary>
        /// <param name="random">The source of randomness.</param>
        /// <param name="family">The program's family.</param>
        /// <param name="name">The rule's name.</param>
        /// <param name="poolSize">The number of root ids family A's constants may name.</param>
        public static ColorRule Rule(Random random, ColorFamily family, string name, int poolSize)
        {
            var rule = new ColorRule(name);
            var monotone = family.History == false;
            if (family.Minting == false)
            {
                Body(random, rule, AllTables, poolSize, monotone);
                Filters(random, rule, family);
                var count = 1 + random.Next(2);
                for (var i = 0; i < count; i++)
                    HeadA(random, rule, family, poolSize);

                CopyARead(random, rule, family);
                return rule;
            }

            switch (random.Next(3))
            {
                case 0:
                {
                    // Lower to lower.
                    Body(random, rule, Lower, 0, monotone);
                    Filters(random, rule, family);
                    var count = 1 + random.Next(2);
                    for (var i = 0; i < count; i++)
                        HeadLower(random, rule, family);

                    CopyARead(random, rule, family);
                    break;
                }
                case 1:
                {
                    // Minting from the lower stratum.
                    Body(random, rule, Lower, 0, monotone);
                    Filters(random, rule, family);
                    var ids = IdVars(rule);
                    if (ids.Count == 0)
                        break;

                    var table = random.Next(2) == 0 ? ColorSchema.Add : ColorSchema.Wrap;
                    var key = table == ColorSchema.Add ? new long[] { Pick(random, ids), Pick(random, ids) } : [Pick(random, ids)];
                    var minted = NewVar(rule, true);
                    rule.Actions.Add(new ColorAction(ColorActionKind.Mint, table, key, minted));
                    switch (random.Next(4))
                    {
                        case 0:
                            rule.Actions.Add(new ColorAction(ColorActionKind.Union, -1, [minted, Pick(random, ids)], -1));
                            break;
                        case 1:
                            rule.Actions.Add(new ColorAction(ColorActionKind.Set, ColorSchema.Path, [minted, Pick(random, ids)], -1));
                            break;
                        default:
                            rule.Actions.Add(new ColorAction(ColorActionKind.Set, ColorSchema.Path, [Pick(random, ids), minted], -1));
                            break;
                    }

                    break;
                }
                default:
                {
                    // Anything to path or unions.
                    Body(random, rule, AllTables, 0, monotone);
                    Filters(random, rule, family);
                    var ids = IdVars(rule);
                    if (ids.Count == 0)
                        break;

                    rule.Actions.Add(random.Next(5) == 0
                        ? new ColorAction(ColorActionKind.Union, -1, [Pick(random, ids), Pick(random, ids)], -1)
                        : new ColorAction(ColorActionKind.Set, ColorSchema.Path, [Pick(random, ids), Pick(random, ids)], -1));
                    break;
                }
            }

            if (rule.Actions.Count == 0)
                rule.Actions.Add(new ColorAction(ColorActionKind.Set, ColorSchema.Mark, [Pick(random, IdVars(rule))], -1));

            return rule;
        }

        /// <summary>
        /// Generates a conjunctive query: a monotone body over every program table and a head writing two of its id
        /// variables into <c>answer</c>.
        /// </summary>
        public static ColorRule Query(Random random, ColorFamily family, string name, int poolSize)
        {
            var rule = new ColorRule(name);
            Body(random, rule, AllTables, family.Minting ? 0 : poolSize, false);
            var ids = IdVars(rule);
            rule.Actions.Add(new ColorAction(ColorActionKind.Set, ColorSchema.Answer, [Pick(random, ids), Pick(random, ids)], -1));
            return rule;
        }

        /// <summary>
        /// Adds one to three atoms over <paramref name="tables"/>, each after the first joined on an earlier variable,
        /// with an id constant from the pool now and then (none when <paramref name="poolSize"/> is 0). When
        /// <paramref name="monotone"/>, a <c>low</c> atom binds its value to a fresh variable nothing else reads.
        /// </summary>
        static void Body(Random random, ColorRule rule, int[] tables, int poolSize, bool monotone)
        {
            var count = random.Next(10) switch
            {
                < 4 => 1,
                < 8 => 2,
                _ => 3,
            };
            for (var a = 0; a < count; a++)
            {
                var table = tables[random.Next(tables.Length)];
                var isId = ColorSchema.IdColumns[table];
                var entries = new long[isId.Length];
                var join = a > 0 ? Array.IndexOf(isId, true) : -1;
                for (var c = 0; c < entries.Length; c++)
                {
                    if (isId[c] == false)
                    {
                        entries[c] = table == ColorSchema.Num && random.Next(5) < 2 ? ColorRule.Constant((ulong)random.Next(4))
                            : table == ColorSchema.Low && monotone ? NewVar(rule, false)
                            : ValueVar(random, rule, monotone);
                        continue;
                    }

                    var ids = IdVars(rule);
                    var roll = random.Next(20);
                    if (poolSize > 0 && roll == 0)
                        entries[c] = ColorRule.Constant((ulong)random.Next(poolSize));
                    else if (ids.Count > 0 && (c == join ? roll < 18 : roll < 5))
                        entries[c] = Pick(random, ids);
                    else
                        entries[c] = NewVar(rule, true);
                }

                rule.Atoms.Add(new ColorAtom(table, entries));
            }

            if (IdVars(rule).Count == 0)
                rule.Atoms.Add(new ColorAtom(ColorSchema.Mark, [NewVar(rule, true)]));
        }

        /// <summary>
        /// Adds, in the history family, an assert-ne filter now and then, and in family A an id-order filter.
        /// </summary>
        static void Filters(Random random, ColorRule rule, ColorFamily family)
        {
            var ids = IdVars(rule);
            if (family.History == false || ids.Count < 2)
                return;

            if (random.Next(10) < 3)
                rule.Filters.Add(new ColorFilter(ColorFilterKind.Ne, ids[random.Next(ids.Count)], ids[random.Next(ids.Count)]));

            if (family.Minting == false && random.Next(10) < 2)
                rule.Filters.Add(new ColorFilter(ColorFilterKind.Less, ids[random.Next(ids.Count)], ids[random.Next(ids.Count)]));
        }

        /// <summary>
        /// Adds one family A action.
        /// </summary>
        static void HeadA(Random random, ColorRule rule, ColorFamily family, int poolSize)
        {
            var ids = IdVars(rule);
            long Id() => poolSize > 0 && random.Next(25) == 0 ? ColorRule.Constant((ulong)random.Next(poolSize)) : Pick(random, ids);

            var roll = random.Next(20);
            if (roll < 2)
                rule.Actions.Add(new ColorAction(ColorActionKind.Union, -1, [Id(), Id()], -1));
            else if (roll < 5)
                rule.Actions.Add(new ColorAction(ColorActionKind.Set, random.Next(2) == 0 ? ColorSchema.Add : ColorSchema.Wrap, random.Next(2) == 0 ? [Id(), Id(), Id()] : [Id(), Id()], -1));
            else if (roll < 10)
                rule.Actions.Add(new ColorAction(ColorActionKind.Set, ColorSchema.Path, [Id(), Id()], -1));
            else if (roll < 13)
                rule.Actions.Add(new ColorAction(ColorActionKind.Set, random.Next(2) == 0 ? ColorSchema.Mark : ColorSchema.Edge, random.Next(2) == 0 ? [Id()] : [Id(), Id()], -1));
            else
                rule.Actions.Add(new ColorAction(ColorActionKind.Set, ColorSchema.Low, [Id(), LowValue(random, rule, family, ids)], -1));

            // A set whose width does not fit its table is redrawn as a path row.
            var last = rule.Actions[^1];
            if (last.Kind == ColorActionKind.Set && last.Args.Length != ColorSchema.IdColumns[last.Table].Length)
                rule.Actions[^1] = new ColorAction(ColorActionKind.Set, ColorSchema.Path, [Id(), Id()], -1);
        }

        /// <summary>
        /// Adds one family B action that writes the lower stratum, or a union.
        /// </summary>
        static void HeadLower(Random random, ColorRule rule, ColorFamily family)
        {
            var ids = IdVars(rule);
            var roll = random.Next(10);
            if (roll < 2)
                rule.Actions.Add(new ColorAction(ColorActionKind.Union, -1, [Pick(random, ids), Pick(random, ids)], -1));
            else if (roll < 5)
                rule.Actions.Add(new ColorAction(ColorActionKind.Set, ColorSchema.Edge, [Pick(random, ids), Pick(random, ids)], -1));
            else if (roll < 7)
                rule.Actions.Add(new ColorAction(ColorActionKind.Set, ColorSchema.Mark, [Pick(random, ids)], -1));
            else
                rule.Actions.Add(new ColorAction(ColorActionKind.Set, ColorSchema.Low, [Pick(random, ids), LowValue(random, rule, family, ids)], -1));
        }

        /// <summary>
        /// In the history family, now and then, copies what an id variable's <c>low</c> reads into another's: the
        /// action reads a table a descendant can lower, so the rule must not inherit a watermark.
        /// </summary>
        static void CopyARead(Random random, ColorRule rule, ColorFamily family)
        {
            var ids = IdVars(rule);
            if (family.History == false || ids.Count == 0 || random.Next(10) < 6)
                return;

            var read = NewVar(rule, false);
            rule.Actions.Add(new ColorAction(ColorActionKind.Read, ColorSchema.Low, [Pick(random, ids)], read));
            rule.Actions.Add(new ColorAction(ColorActionKind.Set, ColorSchema.Low, [Pick(random, ids), read], -1));
        }

        /// <summary>
        /// Picks the value a <c>low</c> row is set to: a body value variable, a constant, or in the history family the
        /// result of a read (<see cref="ColorActionKind.Read"/>) of an id variable's <c>low</c>.
        /// </summary>
        static long LowValue(Random random, ColorRule rule, ColorFamily family, List<int> ids)
        {
            if (family.History && random.Next(3) == 0)
            {
                var read = NewVar(rule, false);
                rule.Actions.Add(new ColorAction(ColorActionKind.Read, ColorSchema.Low, [Pick(random, ids)], read));
                return read;
            }

            var values = Enumerable.Range(0, rule.VarIsId.Count).Where(v => rule.VarIsId[v] == false && rule.Actions.All(a => a.Result != v) && (family.History || IsLatticeOutput(rule, v) == false)).ToList();
            return values.Count > 0 && random.Next(3) > 0 ? values[random.Next(values.Count)] : ColorRule.Constant((ulong)random.Next(10));
        }

        /// <summary>
        /// Gets the id variables the body binds.
        /// </summary>
        static List<int> IdVars(ColorRule rule)
        {
            var bound = new HashSet<long>(rule.Atoms.SelectMany(a => a.Entries).Where(e => e >= 0));
            return Enumerable.Range(0, rule.VarIsId.Count).Where(v => rule.VarIsId[v] && bound.Contains(v)).ToList();
        }

        /// <summary>
        /// Gets whether variable <paramref name="v"/> binds the value of a <c>low</c> atom in the body.
        /// </summary>
        static bool IsLatticeOutput(ColorRule rule, int v) => rule.Atoms.Any(a => a.Table == ColorSchema.Low && a.Entries[1] == v);

        /// <summary>
        /// Picks a value variable for a value column: usually a new one; when <paramref name="monotone"/>, never the
        /// value a <c>low</c> atom binds.
        /// </summary>
        static long ValueVar(Random random, ColorRule rule, bool monotone)
        {
            var values = Enumerable.Range(0, rule.VarIsId.Count).Where(v => rule.VarIsId[v] == false && (monotone == false || IsLatticeOutput(rule, v) == false)).ToList();
            return values.Count > 0 && random.Next(10) < 3 ? values[random.Next(values.Count)] : NewVar(rule, false);
        }

        /// <summary>
        /// Adds a variable and returns its index.
        /// </summary>
        static int NewVar(ColorRule rule, bool isId)
        {
            rule.VarIsId.Add(isId);
            return rule.VarIsId.Count - 1;
        }

        /// <summary>
        /// Picks one of <paramref name="vars"/>.
        /// </summary>
        static long Pick(Random random, List<int> vars) => vars[random.Next(vars.Count)];

    }

}
