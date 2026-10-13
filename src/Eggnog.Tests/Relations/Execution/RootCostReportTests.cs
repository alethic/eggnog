using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What the root costs, written to a report file (<c>RC_OUT</c>, or <c>rootcost.txt</c> beside the tests) so the
    /// same source can be compared between builds of the engine: the exact allocation of root rule-set builds and runs,
    /// and the sizes of the executor's per-node, per-prober and per-entry types.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class RootCostReportTests
    {

        static readonly string Report = Environment.GetEnvironmentVariable("RC_OUT") ?? Path.Combine(AppContext.BaseDirectory, "rootcost.txt");

        static void Log(string line)
        {
            lock (Report)
                File.AppendAllText(Report, line + Environment.NewLine);
        }

        static (RuleSetBuilder Builder, Database Db) Triangle(Database db, TableId e, ExternalFunctionId f, int rules)
        {
            var rsb = db.NewRuleSet();
            for (var r = 0; r < rules; r++)
            {
                var q = rsb.NewRule();
                q.Strategy = PlanStrategy.Gj;
                q.NoDecomposition = true;
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var z = q.NewVariable("z");
                q.AddAtom(e, [x, y]);
                q.AddAtom(e, [y, z]);
                q.AddAtom(e, [z, x]);
                var rb = q.Build();
                rb.CallExternal(f, [x, y, z]);
                rb.Build("tri" + r);
            }

            return (rsb, db);
        }

        [Theory]
        [InlineData("Serial")]
        [InlineData("Parallel")]
        public void TriangleRootBuildAndRun(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase("Serial");
            db.ParallelRuleSetsOverride = mode == "Parallel";
            var e = db.AddTable(Strict(2, 2));
            var rng = new Random(3);
            DatabaseTestUtil.Stage(db, e, Enumerable.Range(0, 6000).Select(_ => new[] { (uint)rng.Next(400), (uint)rng.Next(400) }).ToArray());
            db.MergeAll();
            long calls = 0;
            var f = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                result = 0;
                return true;
            }));

            // A cached plan, as the bridge uses them.
            var (seed, _) = Triangle(db, e, f, 1);
            CachedPlan cached;
            using (var rs0 = seed.Build())
                cached = rs0.BuildCachedPlan(new RuleId(0));

            long buildBytes = 0, runBytes = 0, cachedBuildBytes = 0;
            long runTicks = long.MaxValue;
            for (var it = 0; it < 12; it++)
            {
                var (rsb, _) = Triangle(db, e, f, 1);
                var before = GC.GetAllocatedBytesForCurrentThread();
                var rs = rsb.Build();
                var built = GC.GetAllocatedBytesForCurrentThread() - before;

                var crsb = db.NewRuleSet();
                before = GC.GetAllocatedBytesForCurrentThread();
                for (var k = 0; k < 8; k++)
                    crsb.AddRuleFromCachedPlan(cached);

                using var crs = crsb.Build();
                var cachedBuilt = GC.GetAllocatedBytesForCurrentThread() - before;

                before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                db.RunRuleSet(rs);
                sw.Stop();
                var ran = GC.GetAllocatedBytesForCurrentThread() - before;
                rs.Dispose();
                if (it >= 2)
                {
                    buildBytes += built;
                    runBytes += ran;
                    cachedBuildBytes += cachedBuilt;
                    runTicks = Math.Min(runTicks, sw.ElapsedTicks);
                }
            }

            Log($"triangle {mode}: build {buildBytes / 10} B, cached-build(8 rules) {cachedBuildBytes / 10} B, run {runBytes / 10} B, best run {runTicks * 1000.0 / Stopwatch.Frequency:0.000} ms, calls {calls}");
        }

        [Theory]
        [InlineData("Serial")]
        [InlineData("Parallel")]
        public void AcSaturationRootTotals(string mode)
        {
            long bytes = 0;
            double best = double.MaxValue;
            for (var it = 0; it < 4; it++)
            {
                var g = new ExecutionMathEgraph("Serial");
                g.Db.ParallelRuleSetsOverride = mode == "Parallel";
                var before = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                AcSaturation.Run(g.Db, g.Num, g.Add, g.Uf, g.IdCounter, PlanStrategy.Gj, 7);
                sw.Stop();
                var total = GC.GetTotalAllocatedBytes(true) - before;
                if (it >= 1)
                {
                    bytes += total;
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                }
            }

            Log($"ac-saturation(7) {mode}: {bytes / 3} B total, best {best:0.0} ms");
        }

        [Fact]
        public void Sizes()
        {
            var pool = new TrieNodePool(true);
            for (var i = 0; i < 100; i++)
                pool.Rent(Subset.Dense(0, 10));

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                pool.Rent(Subset.Dense(0, 10));

            var perNode = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000.0;
            File.AppendAllText(Report, $"sizes: TrieNode rent (concurrent) {perNode:0.0} B, Prober {Unsafe.SizeOf<Prober>()} B, QueryEntry {Unsafe.SizeOf<QueryEntry>()} B, FrameUpdate {Unsafe.SizeOf<FrameUpdate>()} B{Environment.NewLine}");
        }

    }

}
