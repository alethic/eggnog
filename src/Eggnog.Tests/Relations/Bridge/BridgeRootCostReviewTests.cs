using System;
using System.Linq;
using System.Reflection;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probes (root-cost lens, colors M10.3): what the store-backed bridge root and its children allocate on the
    /// paths M10.3 changed. The root's egglog-compatible API must pay nothing beyond colored-contexts spec §14.1 (nothing
    /// per staged insert), and a child must cost in proportion to what it touches (§14.2), not to what the e-graph
    /// defines. Allocation is measured on the calling thread; nothing is timed.
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class BridgeRootCostReviewTests
    {

        /// <summary>
        /// Runs <paramref name="body"/> to warm it, then returns the bytes one call allocates on this thread: the least of
        /// up to three windows of <paramref name="runs"/> calls, so a pool trim on a loaded machine does not count.
        /// </summary>
        static long PerCall(Action body, int runs)
        {
            for (var i = 0; i < 5; i++)
                body();

            var least = long.MaxValue;
            for (var attempt = 0; attempt < 3 && least > 0; attempt++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < runs; i++)
                    body();

                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return least / runs;
        }

        /// <summary>
        /// Returns the bytes one call of <paramref name="body"/> allocates on this thread over one window of
        /// <paramref name="runs"/> calls, with no warm-up.
        /// </summary>
        static long Window(Action body, int runs)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < runs; i++)
                body();

            return (GC.GetAllocatedBytesForCurrentThread() - before) / runs;
        }

        /// <summary>
        /// A root holding 1000 edges between 1001 fresh ids, and the rows that restate them.
        /// </summary>
        static (ContextGraph Graph, (FunctionId Function, Value[] Row)[] Rows) LoadedRoot(int count)
        {
            var g = new ContextGraph();
            var ids = Enumerable.Range(0, count + 1).Select(_ => g.EGraph.FreshId()).ToArray();
            var rows = Enumerable.Range(0, count).Select(i => (g.Edge, new[] { ids[i], ids[i + 1], g.Unit })).ToArray();
            g.EGraph.AddValues(rows);
            return (g, rows);
        }

        /// <summary>
        /// The egglog-compatible <see cref="EGraph.AddValues"/> at the root allocates what staging the same rows directly
        /// into an execution state and flushing allocates (egglog's <c>add_values</c>, and M8's port of it), within a
        /// quarter: nothing per row beyond the staged copy the merge needs (§14.1: nothing is added per staged insert).
        /// </summary>
        /// <remarks>
        /// Found by the M10.3 root-cost review: <c>EGraphContext.AddValuesCore</c> copies every row into a temporary
        /// <c>List</c> before staging it, about 33 bytes per row (49.8 KB for 1000 rows against 16.8 KB for the M8 base
        /// and for direct staging).
        /// </remarks>
        [Fact]
        public void RootAddValuesAllocatesNoMoreThanStagingTheRowsDirectly()
        {
            var (g, rows) = LoadedRoot(1000);
            var egraph = g.EGraph;
            var info = egraph.GetFunctionInfo(g.Edge);
            var math = info.Math;
            var table = info.Table;

            void Direct()
            {
                egraph.WithExecutionState(state =>
                {
                    Span<uint> full = stackalloc uint[math.TableColumns];
                    var timestamp = egraph.NextTimestamp;
                    foreach (var (_, row) in rows)
                    {
                        for (var c = 0; c < row.Length; c++)
                            full[c] = row[c].Rep;

                        math.WriteTail(full, timestamp);
                        state.StageInsert(table, full);
                    }

                    return 0;
                });
                egraph.FlushUpdates();
            }

            void ViaAddValues() => egraph.AddValues(rows);

            // The two paths are measured in interleaved windows, and the least window of each is kept, so a pool trim or a
            // GC-driven reallocation under a loaded suite, which only adds bytes, inflates neither for long; the probe stops
            // as soon as the bound holds.
            for (var i = 0; i < 5; i++)
            {
                Direct();
                ViaAddValues();
            }

            var direct = long.MaxValue;
            var viaAddValues = long.MaxValue;
            for (var round = 0; round < 10; round++)
            {
                direct = Math.Min(direct, Window(Direct, 20));
                viaAddValues = Math.Min(viaAddValues, Window(ViaAddValues, 20));
                if (viaAddValues <= direct + direct / 4 + 1024)
                    break;
            }

            Assert.True(viaAddValues <= direct + direct / 4 + 1024, $"AddValues of {rows.Length} rows allocated {viaAddValues} bytes; staging them directly allocated {direct}.");
        }

        /// <summary>
        /// After a bulk load through the egglog-compatible <see cref="EGraph.AddValues"/>, the root keeps no staging
        /// storage proportional to the batch: the rows live in the table, and nothing else of the batch is retained for
        /// the e-graph's lifetime (M8 retained nothing; §14.1 adds nothing per staged insert).
        /// </summary>
        /// <remarks>
        /// Found by the M10.3 root-cost review: the root's <see cref="HostWrites"/> keeps its arenas across flushes, so a
        /// 200,000-row <c>AddValues</c> leaves about 6.5 MB of staging arrays on the root forever (measured with
        /// <c>GC.GetTotalMemory</c> against the M8 base: 19.4 MB against 12.8 MB retained in a Debug build). Read through
        /// reflection: the arenas are private.
        /// </remarks>
        [Fact]
        public void TheRootKeepsNoStagingStorageAfterABulkLoad()
        {
            var (g, _) = LoadedRoot(100_000);
            Assert.Equal(100_000, g.EGraph.TableSize(g.Edge));

            var hostWrites = typeof(EGraphContext).GetField("hostWrites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(g.Root)!;
            long retained = 0;
            foreach (var field in hostWrites.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.GetValue(hostWrites) is Array array)
                    retained += array.Length;

            Assert.True(retained <= 16 * 1024, $"The root retains {retained} array elements of staging storage after a 100,000-row AddValues.");
        }

        /// <summary>
        /// A child's first rule run allocates the same whether the e-graph defines 1 rule or 4001: the per-rule progress
        /// a child keeps (§10.2) grows with the rules it runs, not with every rule ever defined (§14.2: a child costs in
        /// proportion to what it touches).
        /// </summary>
        /// <remarks>
        /// Found by the M10.3 root-cost review: <c>EGraphContext.Commit</c> grows the progress array to
        /// <c>RuleTable.Capacity</c> at the first commit, 16 bytes per defined rule (about 64 KB per child here), although
        /// the child ran rule 0 only.
        /// </remarks>
        [Fact]
        public void AChildsFirstRunDoesNotPayForEveryDefinedRule()
        {
            static long ChildRun(int extraRules)
            {
                var g = new ContextGraph();
                var rule = g.EdgeToPath();
                for (var i = 0; i < extraRules; i++)
                    g.Transitive(name: $"unused {i}");

                var a = g.Root.FreshId();
                var b = g.Root.FreshId();
                g.Root.AddValues([(g.Edge, [a, b, g.Unit])]);
                g.Root.Freeze([rule]);
                return PerCall(() =>
                {
                    using var child = g.Root.CreateChild();
                    child.RunRules(rule);
                }, 20);
            }

            var few = ChildRun(0);
            var many = ChildRun(4000);
            Assert.True(many - few <= 4096, $"A child's first run allocated {few} bytes with 1 rule defined and {many} with 4001.");
        }

        /// <summary>
        /// Guard: what a child of a frozen root allocates to be created, write one lattice row, flush, run a rule whose
        /// watermark it inherits, read a size and be disposed does not grow with the root (§14.2): the same with 10 root
        /// edges as with 20,000.
        /// </summary>
        [Fact]
        public void AChildsWorkDoesNotGrowWithTheFrozenRoot()
        {
            static long ChildWork(int rows)
            {
                var g = new ContextGraph();
                var rule = g.EdgeToPath();
                var ids = Enumerable.Range(0, rows + 1).Select(_ => g.EGraph.FreshId()).ToArray();
                g.EGraph.AddValues(Enumerable.Range(0, rows).Select(i => (g.Edge, new[] { ids[i], ids[i + 1], g.Unit })).ToArray());
                g.Root.RunRules(rule);
                g.Root.Freeze([rule]);
                Assert.True(g.EGraph.GetRuleInfo(rule).AllowInheritance);
                var low = g.Int(5);
                return PerCall(() =>
                {
                    using var child = g.Root.CreateChild();
                    child.StageRow(g.Low, [ids[0], low]);
                    child.FlushUpdates();
                    Assert.Equal(0, child.RunRules(rule).RuleSetReport.NumMatches("edge-path"));
                    Assert.Equal(rows, child.TableSize(g.Path));
                }, 20);
            }

            var small = ChildWork(10);
            var large = ChildWork(20_000);
            Assert.True(large <= small + 2048, $"A child's work allocated {small} bytes under a root of 10 edges and {large} under 20,000.");
        }

        /// <summary>
        /// Guard: the root's reads allocate nothing, through the egglog-compatible API and through the root context
        /// (§10.5), once warm.
        /// </summary>
        [Fact]
        public void RootReadsAllocateNothing()
        {
            var (g, rows) = LoadedRoot(1000);
            var egraph = g.EGraph;
            var root = g.Root;
            var key = rows[3].Row[..2];
            var id = rows[5].Row[0];

            Assert.Equal(0, PerCall(() => egraph.LookupId(g.Edge, key), 1000));
            Assert.Equal(0, PerCall(() => egraph.TableSize(g.Edge), 1000));
            Assert.Equal(0, PerCall(() => egraph.GetCanonInUf(id), 1000));
            Assert.Equal(0, PerCall(() => { var v = new CountingRowVisitor(int.MaxValue); egraph.ForEach(g.Edge, ref v); }, 50));
            Assert.Equal(0, PerCall(() => root.LookupId(g.Edge, key), 1000));
            Assert.Equal(0, PerCall(() => root.TableSize(g.Edge), 1000));
            Assert.Equal(0, PerCall(() => root.Find(id), 1000));
            Assert.Equal(0, PerCall(() => { var v = new CountingRowVisitor(int.MaxValue); root.ForEach(g.Edge, ref v); }, 50));
        }

        /// <summary>
        /// Guard: a warm on-demand extraction query allocates nothing at the root, through the root API and through the
        /// context form over the root (§10.10: per query the watch reads O(slots) versions; per node nothing).
        /// </summary>
        [Fact]
        public void RootExtractionQueriesAllocateNothing()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var leaf = fx.Constructor("Leaf", e, 1, fx.I64);
            var node = fx.Constructor("Node", e, 1, e, e);
            var top = fx.Make(leaf, fx.Int(0));
            for (var i = 1; i < 50; i++)
                top = fx.Make(node, top, fx.Make(leaf, fx.Int(i)));

            var rootApi = Extractor.CreateOnDemand(fx.Schema);
            var contextForm = Extractor.CreateOnDemand(fx.Schema, fx.EGraph.Root);
            Assert.True(rootApi.TryGetCost(e, top, out var expected));
            Assert.True(contextForm.TryGetCost(e, top, out var cost));
            Assert.Equal(expected, cost);

            Assert.Equal(0, PerCall(() => rootApi.TryGetCost(e, top, out _), 1000));
            Assert.Equal(0, PerCall(() => contextForm.TryGetCost(e, top, out _), 1000));
        }

    }

}
