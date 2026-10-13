using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Regressions the differential suite (<see cref="DifferentialTests"/>) found, reduced to fixed programs. They run
    /// with the executor stress tests, since some go parallel.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class DifferentialRegressionTests
    {

        /// <summary>
        /// Soak seed 55299 (bulk profile): a three-atom path <c>t1(.., v1, ..), t0(v1, v2, ..), t1(.., v2, ..)</c> planned
        /// with Free Join while <c>t0</c> was the smallest table, so its stage comes first and probes both outer atoms,
        /// whose own stages probe nothing. Reused from the cached plan after <c>t0</c> grew a little past <c>t1</c>,
        /// egglog's run-time sort put both outer stages first (equal refinement, smaller): a cross product of the two
        /// outer covers with the middle cover scanned in full under each pair, about 400M row visits for 6k matches. The
        /// outer stages are probed by the middle one, so they now wait for it (see <see cref="StageOrdering"/>, the fix
        /// of execute.rs:2399-2470).
        /// </summary>
        [Fact]
        public void ProbedFreeJoinStagesWaitForTheirProberOnAPath()
        {
            // The plan the soak printed: [0] FusedIntersect a1 bind[v1, v2, v6] probe[a0[c3] key[0], a2[c2] key[1]];
            // [1] FusedIntersect a0 bind[v0, v4, v5]; [2] FusedIntersect a2 bind[v7, v3, v8].
            JoinStage[] stages =
            [
                new FusedIntersectStage(Cover(1, 0, 1, 2), [Bind(0, 1), Bind(1, 2), Bind(2, 6)], [Probe(0, 3, 0), Probe(2, 2, 1)]),
                new FusedIntersectStage(Cover(0, 0, 1, 2), [Bind(0, 0), Bind(1, 4), Bind(2, 5)], []),
                new FusedIntersectStage(Cover(2, 0, 1, 3), [Bind(0, 7), Bind(1, 3), Bind(3, 8)], []),
            ];

            // The grown sizes: the outer atoms 723 rows each, the middle one 758. egglog's key gives [1, 2, 0].
            var sizes = new FixedSizes([723, 758, 723]);
            var order = new InstrOrder(3);
            StageOrdering.SortPlanBySize(order, new bool[3], 0, stages, ref sizes, new long[3]);
            Assert.Equal([0, 1, 2], order.ToArray());
        }

        /// <summary>
        /// Soak seed 243399 (huge profile): a cached two-atom join whose plan probes the first atom from the second's
        /// stage, reused after a load left the first atom smaller. egglog's sort ran the first atom's stage first, so the
        /// second stage scanned its 20000-row cover under each of the first atom's 20000 rows (39 s in a debug build). The
        /// probed stage now waits for its prober.
        /// </summary>
        [Fact]
        public void ProbedFreeJoinStageWaitsForItsProber()
        {
            JoinStage[] stages =
            [
                new FusedIntersectStage(Cover(1, 0, 1, 2), [Bind(0, 2), Bind(1, 0), Bind(2, 3)], [Probe(0, 1, 1)]),
                new FusedIntersectStage(Cover(0, 0, 2), [Bind(0, 1), Bind(2, 4)], []),
            ];

            // egglog's key gives [1, 0]: the smaller cover first.
            var sizes = new FixedSizes([18_000, 21_000]);
            var order = new InstrOrder(2);
            StageOrdering.SortPlanBySize(order, new bool[2], 0, stages, ref sizes, new long[2]);
            Assert.Equal([0, 1], order.ToArray());
        }

        /// <summary>
        /// Stages that probe nothing and are probed by nothing still sort by egglog's key, before and after the stages
        /// tied up in probes.
        /// </summary>
        [Fact]
        public void IndependentFreeJoinStagesStillSortBySize()
        {
            JoinStage[] stages =
            [
                new FusedIntersectStage(Cover(0, 0), [Bind(0, 0)], [Probe(1, 0, 0)]),
                new FusedIntersectStage(Cover(1, 0), [Bind(1, 1)], []),
                new FusedIntersectStage(Cover(2, 0), [Bind(0, 2)], []),
            ];

            // a2 is independent and smallest: first. Then a0, whose probe makes a1 wait although a1 is smaller.
            var sizes = new FixedSizes([50, 10, 5]);
            var order = new InstrOrder(3);
            StageOrdering.SortPlanBySize(order, new bool[3], 0, stages, ref sizes, new long[3]);
            Assert.Equal([2, 0, 1], order.ToArray());
        }

        /// <summary>
        /// Found by review of the fix above: the eligibility check rescanned every unscheduled stage's probes for every
        /// comparison of the selection sort, O(stages³ × probes) on the executor's per-frame path. It is now kept as a
        /// count per atom of the probes of the unscheduled stages. Property test: on random Free Join stage lists (random
        /// covers, shared ones included, random probes, cyclic, repeated and self-probes included, sizes with many ties,
        /// random starting orders and start positions), the sort gives exactly the order of the definition, which rescans
        /// the unscheduled stages, through both overloads.
        /// </summary>
        [Fact]
        public void IncrementalEligibilityMatchesItsDefinition()
        {
            var rng = new Random(2024);
            for (var trial = 0; trial < 5000; trial++)
            {
                var n = rng.Next(1, 14);
                var atoms = n + rng.Next(3);
                var stages = new FusedIntersectStage[n];
                // Covers are mostly distinct, as Free Join plans make them; now and then two stages share one, and a
                // stage may probe an atom twice or probe its own cover.
                var covers = Enumerable.Range(0, atoms).OrderBy(_ => rng.Next()).ToArray();
                for (var s = 0; s < n; s++)
                {
                    if (s > 0 && rng.Next(8) == 0)
                        covers[s] = covers[rng.Next(s)];

                    var probes = Enumerable.Range(0, rng.Next(4)).Select(_ => rng.Next(atoms)).Select(a => Probe(a, 0, 0)).ToArray();
                    var columns = Enumerable.Range(0, 1 + rng.Next(2)).ToArray();
                    stages[s] = new FusedIntersectStage(Cover(covers[s], columns), columns.Select(c => Bind(c, s * 2 + c)).ToArray(), probes);
                }

                var sizeArray = Enumerable.Range(0, atoms).Select(_ => rng.Next(1, 4)).ToArray();
                var start = rng.Next(n + 1);
                var initial = new InstrOrder(n);
                for (var k = 0; k < n; k++)
                    initial.Swap(k, rng.Next(k, n));

                var expected = ReferenceSort(stages, initial.ToArray(), start, sizeArray, atoms);
                var sizes = new FixedSizes(sizeArray);
                var viaPool = initial.Clone();
                StageOrdering.SortPlanBySize(viaPool, new bool[n], start, stages, ref sizes, new long[atoms]);
                Assert.Equal(expected, viaPool.ToArray());

                // The executor's overload, with scratch left dirty by an earlier sort.
                var viaScratch = initial.Clone();
                var probedBy = Enumerable.Range(0, atoms + 2).Select(_ => rng.Next(-3, 4)).ToArray();
                StageOrdering.SortPlanBySize(viaScratch, new bool[n], start, stages, ref sizes, new long[atoms], probedBy);
                Assert.Equal(expected, viaScratch.ToArray());
            }
        }

        /// <summary>
        /// The run-time sort of a list of Free Join stages as <see cref="StageOrdering"/> defines it: egglog's selection
        /// sort from <paramref name="start"/> by (-refinement, size, -relations), where a stage is eligible unless another
        /// unscheduled stage probes its cover atom, found by rescanning them.
        /// </summary>
        static int[] ReferenceSort(FusedIntersectStage[] stages, int[] initial, int start, int[] sizes, int atoms)
        {
            var n = stages.Length;
            var order = (int[])initial.Clone();
            if (n - start <= 1)
                return order;

            var refined = new long[atoms];
            void Count(FusedIntersectStage f)
            {
                refined[f.Cover.Atom.Index] += f.Cover.ToIndex.Columns.Length;
                foreach (var p in f.ToIntersect)
                    refined[p.Scan.Atom.Index] += p.Scan.ToIndex.Columns.Length;
            }

            for (var p = 0; p < start; p++)
                Count(stages[order[p]]);

            for (var i = start; i < n; i++)
            {
                bool Eligible(int s) => Enumerable.Range(i, n - i).Any(p => order[p] != s && stages[order[p]].ToIntersect.Any(x => x.Scan.Atom.Index == stages[s].Cover.Atom.Index)) == false;
                (long Refine, long Size, int Rels) Key(int s) => (refined[stages[s].Cover.Atom.Index], sizes[stages[s].Cover.Atom.Index], stages[s].ToIntersect.Length + 1);

                var haveI = Eligible(order[i]);
                var best = Key(order[i]);
                for (var j = i + 1; j < n; j++)
                {
                    if (Eligible(order[j]) == false)
                        continue;

                    var key = Key(order[j]);
                    if (haveI == false || key.Refine > best.Refine || (key.Refine == best.Refine && (key.Size < best.Size || (key.Size == best.Size && key.Rels > best.Rels))))
                    {
                        (order[i], order[j]) = (order[j], order[i]);
                        haveI = true;
                        best = key;
                    }
                }

                Count(stages[order[i]]);
            }

            return order;
        }

        /// <summary>
        /// The same path end to end: the plan is cached while the middle table is small, the middle table then grows
        /// past the outer ones, and the cached plan must still find exactly the path's matches.
        /// </summary>
        [Theory]
        [InlineData("MinCover")]
        [InlineData("PureSize")]
        [InlineData("Gj")]
        public void CachedPathAfterTheMiddleTableGrows(string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var db = new Database(ParallelHeuristics.Serial);
            var t0 = db.AddTable(Strict(2, 2));
            var t1 = db.AddTable(Strict(2, 2));
            var rng = new Random(7);
            DatabaseTestUtil.Stage(db, t0, Enumerable.Range(0, 10).Select(_ => new[] { (uint)rng.Next(60), (uint)rng.Next(60) }).ToArray());
            DatabaseTestUtil.Stage(db, t1, Enumerable.Range(0, 200).Select(_ => new[] { (uint)rng.Next(60), (uint)rng.Next(60) }).ToArray());
            db.MergeAll();

            var log = new List<uint[]>();
            var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.Strategy = strategy;
            q.NoDecomposition = true;
            var a = q.NewVariable("a");
            var b = q.NewVariable("b");
            var c = q.NewVariable("c");
            var d = q.NewVariable("d");
            q.AddAtom(t1, [a, b]);
            q.AddAtom(t0, [b, c]);
            q.AddAtom(t1, [c, d]);
            var rb = q.Build();
            rb.CallExternal(recorder, [a, b, c, d]);
            var rule = rb.Build("path");
            CachedPlan cached;
            using (var rs = rsb.Build())
                cached = rs.BuildCachedPlan(rule);

            DatabaseTestUtil.Stage(db, t0, Enumerable.Range(0, 400).Select(_ => new[] { (uint)rng.Next(60), (uint)rng.Next(60) }).ToArray());
            db.MergeAll();

            var rsb2 = db.NewRuleSet();
            Assert.NotNull(rsb2.AddRuleFromCachedPlan(cached));
            using var rs2 = rsb2.Build();
            db.RunRuleSet(rs2);

            var middle = DatabaseTestUtil.Rows(db, t0);
            var outer = DatabaseTestUtil.Rows(db, t1);
            var expected = (from x in outer from y in middle where x[1] == y[0] from z in outer where y[1] == z[0] select new[] { x[0], x[1], y[1], z[1] }).ToList();
            Assert.Equal(ExecutionTestUtil.Distinct(expected), ExecutionTestUtil.Distinct(log));
            Assert.Equal(expected.Count, log.Count);
        }

        /// <summary>
        /// Soak seed 108799 (huge profile): a union displaces 6487 and a rebuild of the then empty table consumes it;
        /// then more than 10000 rows are loaded, some mentioning 6487, and the next rebuild takes the incremental path
        /// (a big table, no new unions). egglog's incremental path looks only at rows that mention ids displaced since
        /// the table's last rebuild (core-relations/src/table/rebuild.rs:52-66, 120-195), so those rows were never
        /// canonicalized, while the full path, taken for smaller tables, rewrites them: the result depended on the
        /// table's size. The incremental path now also rewrites the rows appended since the table's last rebuild.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        public void IncrementalRebuildCanonicalizesRowsWrittenAfterTheUnion(int threads)
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable());
            var f = db.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(1)], new MinMerge(new ColumnId(1)), new ParallelHeuristics(threads)));
            DatabaseTestUtil.Stage(db, uf, [6487, 4553, 0]);
            db.MergeAll();
            Assert.False(db.ApplyRebuild(uf, [f], 1));

            // 12000 rows, every tenth mentioning the displaced id.
            DatabaseTestUtil.Stage(db, f, Enumerable.Range(0, 12_000).Select(i => new[] { (uint)i, i % 10 == 0 ? 6487u : (uint)i, 2u }).ToArray());
            db.MergeAll();
            Assert.True(SortedWritesTable.ShouldRebuildIncrementally(0, 12_000, false));

            Assert.True(db.ApplyRebuild(uf, [f], 3));
            var rows = DatabaseTestUtil.Rows(db, f);
            Assert.Equal(12_000, rows.Count);
            Assert.DoesNotContain(rows, r => r[1] == 6487);
            // The rewritten rows: every tenth, and row 6487 itself.
            Assert.Equal(1_201, rows.Count(r => r[1] == 4553 && r[2] == 3));
            Assert.False(db.ApplyRebuild(uf, [f], 4));
        }

        /// <summary>
        /// Found by review of the fix above: the mark of the rows written since the last rebuild was kept once per table,
        /// while the union-find positions it pairs with are kept per rebuilder. A rebuild against a second union-find
        /// moved the mark past rows the first had not canonicalized, so the next incremental rebuild against the first
        /// never rewrote them, and the result depended on the table's size again. The marks are now kept per rebuilder.
        /// Each union-find here displaces one id before the rows are written, and each rebuild must rewrite its own id's
        /// rows whatever the other rebuild did in between.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        public void IncrementalRebuildMarksAreKeptPerRebuilder(int threads)
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable());
            var other = db.AddTable(new DisplacedTable());
            var f = db.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(1)], new MinMerge(new ColumnId(1)), new ParallelHeuristics(threads)));
            DatabaseTestUtil.Stage(db, uf, [6487, 4553, 0]);
            DatabaseTestUtil.Stage(db, other, [9001, 9000, 0]);
            db.MergeAll();

            // Both unions are consumed while the table is empty.
            Assert.False(db.ApplyRebuild(uf, [f], 1));
            Assert.False(db.ApplyRebuild(other, [f], 1));

            // 12000 rows: every tenth mentions 6487, every tenth from the fifth on mentions 9001.
            DatabaseTestUtil.Stage(db, f, Enumerable.Range(0, 12_000).Select(i => new[] { (uint)i, i % 10 == 0 ? 6487u : i % 10 == 5 ? 9001u : (uint)i, 2u }).ToArray());
            db.MergeAll();
            Assert.True(SortedWritesTable.ShouldRebuildIncrementally(0, 12_000, false));

            // The rebuild against the second union-find rewrites only its own id's rows.
            Assert.True(db.ApplyRebuild(other, [f], 3));
            var rows = DatabaseTestUtil.Rows(db, f);
            Assert.DoesNotContain(rows, r => r[1] == 9001);
            Assert.Equal(1_201, rows.Count(r => r[1] == 6487));

            // The rebuild against the first must still rewrite its id's rows, although the other rebuild came after them.
            Assert.True(db.ApplyRebuild(uf, [f], 4));
            rows = DatabaseTestUtil.Rows(db, f);
            Assert.Equal(12_000, rows.Count);
            Assert.DoesNotContain(rows, r => r[1] == 6487 || r[1] == 9001);
            Assert.Equal(1_201, rows.Count(r => r[1] == 4553 && r[2] == 4));
            Assert.Equal(1_201, rows.Count(r => r[1] == 9000 && r[2] == 3));

            // Both are now up to date.
            Assert.False(db.ApplyRebuild(uf, [f], 5));
            Assert.False(db.ApplyRebuild(other, [f], 5));
        }

        static ScanSpec Cover(int atom, params int[] columns) => new(new SubAtom(new AtomId(atom), columns.Select(c => new ColumnId(c)).ToArray()), []);

        static StageBinding Bind(int column, int variable) => new(new ColumnId(column), new Variable(variable));

        static Probe Probe(int atom, int column, int key) => new(new ScanSpec(new SubAtom(new AtomId(atom), [new ColumnId(column)]), []), [new ColumnId(key)]);

    }

}
