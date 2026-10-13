using System;
using System.IO;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a warm child run allocates, which should be nothing per row, probe or frame. These measure what a warm
    /// child's triangle run allocates per match and per layered probe, beside the same query at the root, and write it
    /// to a report file (<c>RC_OUT</c>, or <c>rootcost.txt</c> beside the tests); they assert nothing, so the report is
    /// what compares the child with the root.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class ChildAllocationTests
    {

        static readonly string Report = Environment.GetEnvironmentVariable("RC_OUT") ?? Path.Combine(AppContext.BaseDirectory, "rootcost.txt");

        static (long Bytes, long Calls, long Probes) Measure(Store store, Context context, TableId e, ExternalFunctionId f, string strategyName, Func<long> calls, Action resetCalls)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var db = context.Database;
            long Run()
            {
                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                q.Strategy = strategy;
                q.NoDecomposition = true;
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var z = q.NewVariable("z");
                q.AddAtom(e, [x, y, QueryEntry.Wildcard()]);
                q.AddAtom(e, [y, z, QueryEntry.Wildcard()]);
                q.AddAtom(e, [z, x, QueryEntry.Wildcard()]);
                var rb = q.Build();
                rb.CallExternal(f, [x, y, z]);
                rb.Build("tri");
                using var rs = rsb.Build();
                var epoch = store.ReadClock();
                var before = GC.GetAllocatedBytesForCurrentThread();
                if (context.IsRoot)
                    db.ExecuteRuleSet(rs, ReportLevel.TimeOnly, epoch);
                else
                    db.ExecuteRuleSet(rs, ReportLevel.TimeOnly, epoch);

                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Run();
            Run();
            resetCalls();
            db.ExecutorCounts?.Reset();
            var bytes = Run();
            return (bytes, calls(), db.ExecutorCounts?.LayeredProbes ?? -1);
        }

        [Theory]
        [InlineData("Gj", 1200)]
        [InlineData("Gj", 2400)]
        [InlineData("PureSize", 1200)]
        public void WarmChildTriangle(string strategyName, int childEdges)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var e = store.AddTable(Relation(2), name: "e");
            var root = store.Root;
            var ids = Enumerable.Range(0, 400).Select(_ => root.NewId()).ToArray();
            var rng = new Random(3);
            root.Write(state =>
            {
                for (var i = 0; i < 6000; i++)
                    state.StageInsert(e, [ids[rng.Next(ids.Length)], ids[rng.Next(ids.Length)], state.Timestamp]);

                return 0;
            });

            long calls = 0;
            var f = root.Database.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                calls++;
                result = 0;
                return true;
            }));

            var rootResult = Measure(store, root, e, f, strategyName, () => calls, () => calls = 0);
            root.Freeze();
            using var child = root.CreateChild();
            child.Write(state =>
            {
                for (var i = 0; i < childEdges; i++)
                    state.StageInsert(e, [ids[rng.Next(ids.Length)], ids[rng.Next(ids.Length)], state.Timestamp]);

                return 0;
            });

            var childResult = Measure(store, child, e, f, strategyName, () => calls, () => calls = 0);
            var line = $"child-triangle {strategyName} childEdges={childEdges}: root {rootResult.Bytes} B / {rootResult.Calls} matches = {(double)rootResult.Bytes / rootResult.Calls:0.00} B/match; "
                + $"child {childResult.Bytes} B / {childResult.Calls} matches = {(double)childResult.Bytes / childResult.Calls:0.00} B/match, layered probes {childResult.Probes} ({(double)childResult.Bytes / Math.Max(1, childResult.Probes):0.00} B/probe)";
            File.AppendAllText(Report, line + Environment.NewLine);
        }

    }

}
