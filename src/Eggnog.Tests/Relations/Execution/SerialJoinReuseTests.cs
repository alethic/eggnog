using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The database keeps its serial join between rule sets (<see cref="JoinRun.Rebind"/>, <see cref="JoinRun.Park"/>):
    /// later runs reuse its pooled nodes, per-node indexes and frame buffers and must see exactly what a fresh join
    /// would, including after a run that stopped early or threw, and in a clone.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class SerialJoinReuseTests
    {

        public static IEnumerable<object[]> StrategiesData => new[] { "Gj", "PureSize", "MinCover" }.Select(s => new object[] { s });

        /// <summary>
        /// Transitive closure of a random graph run to a fixpoint, one rule set per iteration on the same database, then
        /// compared with the closure computed directly.
        /// </summary>
        [Theory]
        [MemberData(nameof(StrategiesData))]
        public void RepeatedRunsReachTheClosure(string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            for (var seed = 0; seed < 5; seed++)
            {
                var rng = new Random(seed);
                var db = ExecutionTestUtil.NewDatabase("Serial");
                var edge = db.AddTable(Strict(2, 2));
                var path = db.AddTable(Strict(2, 2));
                var edges = Enumerable.Range(0, 150).Select(_ => new[] { (uint)rng.Next(60), (uint)rng.Next(60) }).ToArray();
                DatabaseTestUtil.Stage(db, edge, edges);
                DatabaseTestUtil.Stage(db, path, edges);
                db.MergeAll();

                var iterations = 0;
                while (true)
                {
                    var rsb = db.NewRuleSet();
                    var q = rsb.NewRule();
                    q.Strategy = strategy;
                    var x = q.NewVariable("x");
                    var y = q.NewVariable("y");
                    var z = q.NewVariable("z");
                    q.AddAtom(path, [x, y]);
                    q.AddAtom(edge, [y, z]);
                    var rb = q.Build();
                    rb.Insert(path, [x, z]);
                    rb.Build("step");
                    using var rs = rsb.Build();
                    iterations++;
                    if (db.RunRuleSet(rs).Changed == false)
                        break;
                }

                Assert.True(iterations > 2, $"seed {seed}: {iterations} iterations");
                Assert.Equal(Show(Closure(edges)), Rows(db, path));
            }
        }

        /// <summary>
        /// A run stopped early leaves its join half-way through; the join is released (<see cref="JoinRun.ReleaseInterrupted"/>)
        /// and kept, and the next run, reusing it, finds every match.
        /// </summary>
        [Theory]
        [MemberData(nameof(StrategiesData))]
        public void ARunAfterAnEarlyStopFindsEveryMatch(string strategyName)
        {
            var (db, edge, expected) = TwoHops(3);
            var stopAfter = 50;
            var calls = 0;
            var f = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                if (++calls == stopAfter)
                    state.TriggerEarlyStop();

                result = 0;
                return true;
            }));

            var first = RunTwoHops(db, edge, f, strategyName);
            Assert.True(first < expected, $"the stopped run matched {first} of {expected}");

            stopAfter = -1;
            for (var run = 0; run < 3; run++)
                Assert.Equal(expected, RunTwoHops(db, edge, f, strategyName));
        }

        /// <summary>
        /// Runs stopped at different depths of the join (the first match, inside the first morsel, after several) alternate
        /// with full runs on one kept join: every full run finds every match, and every stopped run stops.
        /// </summary>
        [Theory]
        [MemberData(nameof(StrategiesData))]
        public void StoppedAndFullRunsAlternateOnOneJoin(string strategyName)
        {
            var (db, edge, expected) = TwoHops(6);
            var stopAfter = -1;
            var calls = 0;
            var f = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                if (++calls == stopAfter)
                    state.TriggerEarlyStop();

                result = 0;
                return true;
            }));

            foreach (var stop in new[] { 1, 7, 129, 300, 1, 2 })
            {
                calls = 0;
                stopAfter = stop;
                Assert.InRange(RunTwoHops(db, edge, f, strategyName), 1, expected - 1);

                calls = 0;
                stopAfter = -1;
                Assert.Equal(expected, RunTwoHops(db, edge, f, strategyName));
            }
        }

        /// <summary>
        /// A run stopped at its first match keeps its join, so the next one reuses the join's leaf-scan buffer instead of
        /// growing a new one as large as the table: the allocation of a stopped run does not grow with the table.
        /// </summary>
        [Fact]
        public void AnEarlyStoppedRunKeepsItsJoin()
        {
            const int small = 1_000;
            const int large = 21_000;
            var perRow = (StoppedRunAllocation(large) - StoppedRunAllocation(small)) / (double)(large - small);
            Assert.True(perRow <= 4, $"A stopped run allocates {perRow:0.#} bytes per row of the table it scans.");
        }

        /// <summary>
        /// The bytes the second of two runs of <c>edge(x, y) =&gt; f(x, y)</c> allocates on this thread, over
        /// <paramref name="rows"/> edges, where <c>f</c> stops the run at its first call.
        /// </summary>
        static long StoppedRunAllocation(int rows)
        {
            var db = ExecutionTestUtil.NewDatabase("Serial");
            var edge = db.AddTable(Strict(2, 2));
            DatabaseTestUtil.Stage(db, edge, Enumerable.Range(0, rows).Select(i => new[] { (uint)i, (uint)(i % 97) }).ToArray());
            db.MergeAll();
            var f = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                state.TriggerEarlyStop();
                result = 0;
                return true;
            }));

            Assert.True(RunEdges(db, edge, f) > 0);
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(RunEdges(db, edge, f) > 0);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>
        /// Runs <c>edge(x, y) =&gt; f(x, y)</c> once and returns its match count.
        /// </summary>
        static long RunEdges(Database db, TableId edge, ExternalFunctionId f)
        {
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(edge, [x, y]);
            var rb = q.Build();
            rb.CallExternal(f, [x, y]);
            rb.Build("edges");
            using var rs = rsb.Build();
            return db.RunRuleSet(rs).NumMatches("edges");
        }

        /// <summary>
        /// An exception thrown by an external function leaves the join in the middle of a rule; the next run starts
        /// from a fresh one and finds every match.
        /// </summary>
        [Theory]
        [MemberData(nameof(StrategiesData))]
        public void ARunAfterAnExceptionFindsEveryMatch(string strategyName)
        {
            var (db, edge, expected) = TwoHops(4);
            var throwAfter = 70;
            var calls = 0;
            var f = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                if (++calls == throwAfter)
                    throw new InvalidOperationException("boom");

                result = 0;
                return true;
            }));

            Assert.Throws<InvalidOperationException>(() => RunTwoHops(db, edge, f, strategyName));
            throwAfter = -1;
            for (var run = 0; run < 3; run++)
                Assert.Equal(expected, RunTwoHops(db, edge, f, strategyName));
        }

        /// <summary>
        /// A clone does not inherit the original's kept join, which reads the original's tables: each database's run
        /// sees its own rows.
        /// </summary>
        [Fact]
        public void ACloneRunsAgainstItsOwnTables()
        {
            var (db, edge, expected) = TwoHops(5);
            var count = 0L;
            var f = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                count++;
                result = 0;
                return true;
            }));

            Assert.Equal(expected, RunTwoHops(db, edge, f, "Gj"));
            var clone = db.Clone();
            DatabaseTestUtil.Stage(clone, edge, [1000, 1001], [1001, 1002]);
            clone.MergeAll();
            Assert.Equal(expected + 1, RunTwoHops(clone, edge, f, "Gj"));
            Assert.Equal(expected, RunTwoHops(db, edge, f, "Gj"));
        }

        /// <summary>
        /// A database of random edges and the number of two-hop paths <c>(x, y, z)</c> through them.
        /// </summary>
        static (Database Db, TableId Edge, long Paths) TwoHops(int seed)
        {
            var rng = new Random(seed);
            var db = ExecutionTestUtil.NewDatabase("Serial");
            var edge = db.AddTable(Strict(2, 2));
            var edges = Enumerable.Range(0, 400).Select(_ => new[] { (uint)rng.Next(40), (uint)rng.Next(40) }).ToArray();
            DatabaseTestUtil.Stage(db, edge, edges);
            db.MergeAll();
            var distinct = edges.Select(e => (e[0], e[1])).Distinct().ToArray();
            var paths = distinct.Sum(a => (long)distinct.Count(b => b.Item1 == a.Item2));
            return (db, edge, paths);
        }

        /// <summary>
        /// Runs <c>edge(x, y), edge(y, z) =&gt; f(x, y, z)</c> once and returns its match count.
        /// </summary>
        static long RunTwoHops(Database db, TableId edge, ExternalFunctionId f, string strategyName)
        {
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.Strategy = Enum.Parse<PlanStrategy>(strategyName);
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            q.AddAtom(edge, [x, y]);
            q.AddAtom(edge, [y, z]);
            var rb = q.Build();
            rb.CallExternal(f, [x, y, z]);
            rb.Build("hops");
            using var rs = rsb.Build();
            return db.RunRuleSet(rs).NumMatches("hops");
        }

        /// <summary>
        /// The transitive closure of <paramref name="edges"/>.
        /// </summary>
        static IEnumerable<uint[]> Closure(uint[][] edges)
        {
            var reach = new HashSet<(uint, uint)>(edges.Select(e => (e[0], e[1])));
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (a, b) in reach.ToArray())
                    foreach (var e in edges)
                        if (e[0] == b)
                            changed |= reach.Add((a, e[1]));
            }

            return reach.OrderBy(p => p.Item1).ThenBy(p => p.Item2).Select(p => new[] { p.Item1, p.Item2 });
        }

    }

}
