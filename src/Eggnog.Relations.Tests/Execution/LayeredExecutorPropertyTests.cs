using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// T8 (colored-contexts spec §17 M10.2): randomized differential tests of the layered executor. Random layered states
    /// (<see cref="SyntheticLayers"/>: two to four levels of the same three tables, each with rows, tombstones, superseded
    /// rows and cached indexes, views that leave layers out and hide rows in frozen and live parts) and random conjunctive
    /// queries (constants, repeated variables, wildcards, comparisons and column equalities, atoms without variables, every
    /// strategy, decomposition on and off, cached plans with extra header constraints) must produce, through
    /// <see cref="Database.RunRuleSet"/>, exactly the distinct bindings of a nested-loop join over the rows the views show
    /// (<see cref="LayerFlattener"/>), and the flat executor's over a database holding those rows.
    /// </summary>
    /// <remarks>
    /// With S6 (key uniqueness, I12) every view shows at most one row per key, as a context's views do: a row whose key a
    /// higher layer shows is hidden, as Lemma F's hides make it. The S6-off variant drops that, so a key may be visible in
    /// several layers; the executor's match equivalence (I27) does not depend on it, only the flat comparison does. Matches
    /// are a multiset, so only distinct bindings are compared (decision 6).
    /// </remarks>
    [Collection(ExecutorStressCollection.Name)]
    public class LayeredExecutorPropertyTests
    {

        static readonly (int KeyCount, int Arity, int SortColumn)[] Shapes = [(1, 3, 2), (2, 3, 2), (1, 2, -1)];

        public static IEnumerable<object[]> ModesData => ExecutionTestUtil.Modes.Select(m => new object[] { m });

        /// <summary>
        /// Small layers (keys below 6, values below 3), up to five atoms.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void SmallLayeredStatesMatchTheFlatJoin(string mode)
        {
            long probes = 0;
            long ancestors = 0;
            for (var seed = 0; seed < 1500; seed++)
            {
                Check(new Random(seed), mode, seed, rowsPerBatch: 5, keys: 6, values: 3, maxAtoms: 5, s6: true, out var p, out var a);
                probes += p;
                ancestors += a;
            }

            AssertLayeredWork(probes, ancestors);
        }

        /// <summary>
        /// Larger layers (keys below 50, up to 50 rows per batch per level), up to three atoms, so candidate subsets cross
        /// the executor's size thresholds in each layer: sparse indexes, eager refinement, child caches under layer
        /// selectors, the layers' cached indexes for large dense ranges, chunked scans and several action batches.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void LargerLayeredStatesMatchTheFlatJoin(string mode)
        {
            long probes = 0;
            long ancestors = 0;
            for (var seed = 0; seed < 200; seed++)
            {
                Check(new Random(1000 + seed), mode, seed, rowsPerBatch: 50, keys: 50, values: 4, maxAtoms: 3, s6: true, out var p, out var a);
                probes += p;
                ancestors += a;
            }

            AssertLayeredWork(probes, ancestors);
        }

        /// <summary>
        /// The S6-off variant: views may show a key in several layers.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void LayeredStatesWithoutKeyUniquenessMatchTheJoin(string mode)
        {
            for (var seed = 0; seed < 500; seed++)
                Check(new Random(5000 + seed), mode, seed, rowsPerBatch: 6, keys: 6, values: 3, maxAtoms: 4, s6: false);

            for (var seed = 0; seed < 60; seed++)
                Check(new Random(7000 + seed), mode, seed, rowsPerBatch: 40, keys: 40, values: 4, maxAtoms: 3, s6: false);
        }

        /// <summary>
        /// Asserts, when the executor counts (<see cref="ExecutorCounters.Enabled"/>), that the seeds reached the layered
        /// paths: probes joined across layers, and scans of ancestor layers.
        /// </summary>
        static void AssertLayeredWork(long layeredProbes, long ancestorRows)
        {
            if (ExecutorCounters.Enabled == false)
                return;

            Assert.True(layeredProbes > 0, "no probe was joined across layers");
            Assert.True(ancestorRows > 0, "no ancestor layer was scanned");
        }

        static void Check(Random rng, string mode, int seed, int rowsPerBatch, int keys, int values, int maxAtoms, bool s6) =>
            Check(rng, mode, seed, rowsPerBatch, keys, values, maxAtoms, s6, out _, out _);

        static void Check(Random rng, string mode, int seed, int rowsPerBatch, int keys, int values, int maxAtoms, bool s6, out long layeredProbes, out long ancestorRows)
        {
            var (db, layers, tables) = RandomLayers(rng, mode, rowsPerBatch, keys, values, s6);
            var atoms = RandomQuery(rng, tables, values, maxAtoms);
            var strategy = (PlanStrategy)rng.Next(3);
            var noDecomposition = rng.Next(2) == 0;
            var context = $"{mode} seed {seed} (s6 {s6}, depth {layers.Depth}), {strategy}, noDecomposition {noDecomposition}: {string.Join("; ", atoms)}";
            var visible = tables.ToDictionary(t => t, t => LayerFlattener.VisibleRows(layers.GetView(t)));

            var log = new List<uint[]>();
            var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var bound = atoms.SelectMany(a => a.Entries).Where(e => e.IsVariable && e.IsWildcard == false).Select(e => e.Variable).Distinct().OrderBy(v => v.Index).ToArray();
            var (rs, rule) = Build(db, atoms, bound, recorder, strategy, noDecomposition);
            using (rs)
            {
                var expected = Reference(visible, atoms, [], bound, out var combinations);
                var report = db.RunRuleSet(rs);
                Assert.True(expected.SetEquals(ExecutionTestUtil.Distinct(log)), context + Describe(rs[rule], expected, log));
                Assert.Equal(log.Count, report.NumMatches("rule"));

                // Multiplicity (I27, decision 6): every match is one path of frames, and the frames of a stage partition the
                // visible rows they refine, so no executor emits more matches than there are combinations of visible rows
                // satisfying the query. A row read twice, or a layer enumerated twice, breaks the bound.
                Assert.True(log.Count <= combinations, context + $" (multiplicity: {log.Count} matches, {combinations} row combinations)" + Describe(rs[rule], expected, log));

                if (s6)
                {
                    // The flat executor over the same rows, through the same plan shape, finds the same bindings.
                    var flat = LayerFlattener.Flatten(Shapes, layers.GetView, mode);
                    var flatLog = new List<uint[]>();
                    var flatRecorder = flat.AddExternalFunction(ExecutionTestUtil.Recorder(flatLog));
                    var (flatRules, _) = Build(flat, atoms, bound, flatRecorder, strategy, noDecomposition);
                    using (flatRules)
                        flat.RunRuleSet(flatRules);

                    Assert.True(ExecutionTestUtil.Distinct(flatLog).SetEquals(ExecutionTestUtil.Distinct(log)), context + " (flat executor)");
                }

                // The same rule from its cached plan, with random extra constraints: the semi-naive pattern, per layer.
                var cached = rs.BuildCachedPlan(rule);
                var extras = RandomExtras(rng, atoms, values);
                log.Clear();
                var rsb2 = db.NewRuleSet();
                var added = rsb2.AddRuleFromCachedPlan(cached, extras.ToArray());
                using var rs2 = rsb2.Build();
                var expectedWithExtras = Reference(visible, atoms, extras, bound, out var combinationsWithExtras);
                db.RunRuleSet(rs2);
                Assert.True(log.Count <= combinationsWithExtras, context + $" with extras {string.Join(", ", extras)} (multiplicity: {log.Count} matches, {combinationsWithExtras} row combinations)");
                if (added is null)
                    Assert.True(expectedWithExtras.Count == 0, context + " (cached variant skipped but matches exist)");
                else
                    Assert.True(expectedWithExtras.SetEquals(ExecutionTestUtil.Distinct(log)), context + $" with extras {string.Join(", ", extras)}" + Describe(rs2[added.Value], expectedWithExtras, log));
            }

            layeredProbes = db.ExecutorCounts?.LayeredProbes ?? 0;
            ancestorRows = db.ExecutorCounts?.AncestorRowsScanned ?? 0;
        }

        static (RuleSet Rules, RuleId Rule) Build(Database db, List<RandomAtom> atoms, Variable[] bound, ExternalFunctionId recorder, PlanStrategy strategy, bool noDecomposition)
        {
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.Strategy = strategy;
            q.NoDecomposition = noDecomposition;
            for (var i = 0; i < 5; i++)
                q.NewVariable("v" + i);

            foreach (var atom in atoms)
                q.AddAtom(atom.Table, atom.Entries, atom.Constraints);

            var rb = q.Build();
            rb.CallExternal(recorder, bound.Select(v => QueryEntry.Var(v)).ToArray());
            var rule = rb.Build("rule");
            return (rsb.Build(), rule);
        }

        static string Describe(RulePlan plan, SortedSet<string> expected, List<uint[]> log)
        {
            var got = ExecutionTestUtil.Distinct(log);
            return $"\n{plan.Template}\nexpected {string.Join(" ", expected)}\ngot      {string.Join(" ", got)}";
        }

        /// <summary>
        /// The nested-loop join over the visible rows, projected onto <paramref name="bound"/>; <paramref name="combinations"/>
        /// receives the number of combinations of visible rows (one per atom) that satisfy the query.
        /// </summary>
        static SortedSet<string> Reference(Dictionary<TableId, List<uint[]>> visible, List<RandomAtom> atoms, List<(AtomId Atom, Constraint Constraint)> extras, Variable[] bound, out long combinations)
        {
            var rows = atoms.Select(a => visible[a.Table]).ToArray();
            var result = new SortedSet<string>(StringComparer.Ordinal);
            var binding = new Dictionary<int, uint>();
            long count = 0;
            Recurse(0);
            combinations = count;
            return result;

            void Recurse(int depth)
            {
                if (depth == atoms.Count)
                {
                    count++;
                    result.Add(string.Join(",", bound.Select(v => binding[v.Index])));
                    return;
                }

                var atom = atoms[depth];
                foreach (var row in rows[depth])
                {
                    if (Constraint.EvaluateAll(atom.Constraints, row) == false)
                        continue;
                    if (extras.Any(x => x.Atom.Index == depth && x.Constraint.Evaluate(row) == false))
                        continue;

                    var added = new List<int>();
                    var ok = true;
                    for (var c = 0; c < row.Length && ok; c++)
                    {
                        var e = atom.Entries[c];
                        if (e.IsVariable == false)
                        {
                            ok = row[c] == e.Constant.Rep;
                        }
                        else if (e.IsWildcard == false)
                        {
                            if (binding.TryGetValue(e.Variable.Index, out var v))
                            {
                                ok = v == row[c];
                            }
                            else
                            {
                                binding[e.Variable.Index] = row[c];
                                added.Add(e.Variable.Index);
                            }
                        }
                    }

                    if (ok)
                        Recurse(depth + 1);

                    foreach (var v in added)
                        binding.Remove(v);
                }
            }
        }

        /// <summary>
        /// Builds a random layered state: levels of the three tables (<c>t0(k; a, ts)</c> and <c>t1(k1, k2; ts)</c> sorted
        /// by their last column, an unsorted <c>t2(k; a)</c>), each filled in timestamp batches with removals and
        /// re-inserts (tombstones and superseded rows) and cached indexes built in between (so they list tombstones), then
        /// views that leave some layers out and hide rows.
        /// </summary>
        static (Database Db, SyntheticLayers Layers, TableId[] Tables) RandomLayers(Random rng, string mode, int rowsPerBatch, int keySpace, int values, bool s6)
        {
            var depth = 1 + rng.Next(3);
            var levels = new Database[depth + 1];
            for (var d = 0; d <= depth; d++)
            {
                var level = d == 0 ? ExecutionTestUtil.NewDatabase(mode) : new Database(ParallelHeuristics.Serial);
                foreach (var (keys, arity, sort) in Shapes)
                    level.AddTable(new SortedWritesTable(keys, arity, sort >= 0 ? C(sort) : null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));

                Fill(rng, level, rowsPerBatch, keySpace, values);
                levels[d] = level;
            }

            var tables = Enumerable.Range(0, Shapes.Length).Select(i => new TableId(i)).ToArray();
            var layers = new SyntheticLayers(levels);
            foreach (var table in tables)
            {
                var keyCount = Shapes[table.Index].KeyCount;
                var present = new bool[depth + 1];
                var frozen = new List<int>?[depth + 1];
                var live = new List<int>?[depth + 1];
                var shown = new HashSet<string>(StringComparer.Ordinal);
                for (var d = depth; d >= 0; d--)
                {
                    present[d] = rng.Next(5) != 0;
                    if (present[d] == false)
                        continue;

                    var data = ((SortedWritesTable)levels[d].GetTable(table)).Rows;
                    var hideMore = rng.Next(4) == 0 ? 2 : 8;
                    for (var r = 0; r < data.Count; r++)
                    {
                        var key = TableTestUtil.KeyOf(data.GetRow(r), keyCount);
                        var hide = rng.Next(hideMore) == 0 || (s6 && data.IsStale(r) == false && shown.Contains(key));
                        if (hide)
                        {
                            var into = rng.Next(2) == 0 ? (frozen[d] ??= new List<int>()) : (live[d] ??= new List<int>());
                            into.Add(r);
                        }
                        else if (data.IsStale(r) == false)
                        {
                            shown.Add(key);
                        }
                    }
                }

                layers.Define(table, present, frozen, live);
            }

            layers.Seal();
            levels[0].AttachView(layers);
            return (levels[0], layers, tables);
        }

        static void Fill(Random rng, Database db, int rowsPerBatch, int keySpace, int values)
        {
            var t0 = new TableId(0);
            var t1 = new TableId(1);
            var t2 = new TableId(2);
            for (uint ts = 0; ts < 3; ts++)
            {
                for (var i = rng.Next(rowsPerBatch + 1); i > 0; i--)
                {
                    DatabaseTestUtil.Stage(db, t0, DatabaseTestUtil.R((uint)rng.Next(keySpace), (uint)rng.Next(values), ts));
                    DatabaseTestUtil.Stage(db, t1, DatabaseTestUtil.R((uint)rng.Next(keySpace), (uint)rng.Next(values), ts));
                    DatabaseTestUtil.Stage(db, t2, DatabaseTestUtil.R((uint)rng.Next(keySpace), (uint)rng.Next(values)));
                }

                db.MergeAll();
                if (rng.Next(2) == 0)
                {
                    foreach (var (table, arity) in new[] { (t0, 3), (t1, 3), (t2, 2) })
                    {
                        var info = db.GetTableInfo(table);
                        for (var c = 0; c < arity; c++)
                            info.GetColumnIndex(C(c));

                        info.GetIndex([C(0), C(1)]);
                        info.GetIndex([C(1), C(0)]);
                    }
                }

                for (var k = rng.Next(3); k > 0; k--)
                {
                    DatabaseTestUtil.StageRemove(db, t0, DatabaseTestUtil.R((uint)rng.Next(keySpace)));
                    DatabaseTestUtil.StageRemove(db, t2, DatabaseTestUtil.R((uint)rng.Next(keySpace)));
                    db.MergeAll();
                }
            }
        }

        static List<RandomAtom> RandomQuery(Random rng, TableId[] tables, int values, int maxAtoms)
        {
            var atoms = new List<RandomAtom>();
            for (var n = 1 + rng.Next(maxAtoms); n > 0; n--)
            {
                var table = tables[rng.Next(tables.Length)];
                var (_, arity, sort) = Shapes[table.Index];
                var entries = new QueryEntry[arity];
                for (var c = 0; c < arity; c++)
                {
                    var roll = rng.Next(10);
                    entries[c] = roll < 7 ? QueryEntry.Var(new Variable(rng.Next(5))) : roll < 8 ? QueryEntry.Const(V((uint)rng.Next(values))) : QueryEntry.Wildcard();
                }

                var constraints = new List<Constraint>();
                for (var k = rng.Next(10) switch { < 6 => 0, < 9 => 1, _ => 2 }; k > 0; k--)
                    constraints.Add(RandomConstraint(rng, arity, sort, values, allowEq: true));

                atoms.Add(new RandomAtom(table, entries, constraints.ToArray()));
            }

            return atoms;
        }

        static List<(AtomId Atom, Constraint Constraint)> RandomExtras(Random rng, List<RandomAtom> atoms, int values)
        {
            var extras = new List<(AtomId, Constraint)>();
            for (var k = rng.Next(3); k > 0; k--)
            {
                var i = rng.Next(atoms.Count);
                var arity = atoms[i].Entries.Length;

                // Cached plans accept only fast constraints: ranges on the sort column, or equalities (promoted).
                var sorted = arity == 3;
                var c = sorted && rng.Next(3) != 0
                    ? RandomConstraint(rng, arity, 2, values, allowEq: false, onlySort: true)
                    : Constraint.EqConst(C(rng.Next(arity)), V((uint)rng.Next(values)));
                extras.Add((new AtomId(i), c));
            }

            return extras;
        }

        static Constraint RandomConstraint(Random rng, int arity, int sort, int values, bool allowEq, bool onlySort = false)
        {
            var column = onlySort ? sort : rng.Next(arity);
            var value = V((uint)rng.Next(onlySort ? 3 : values));
            switch (rng.Next(allowEq ? 6 : 5))
            {
                case 0:
                    return Constraint.EqConst(C(column), value);
                case 1:
                    return Constraint.LtConst(C(column), value);
                case 2:
                    return Constraint.GtConst(C(column), value);
                case 3:
                    return Constraint.LeConst(C(column), value);
                case 4:
                    return Constraint.GeConst(C(column), value);
                default:
                    return Constraint.Eq(C(column), C(rng.Next(arity)));
            }
        }

    }

}
