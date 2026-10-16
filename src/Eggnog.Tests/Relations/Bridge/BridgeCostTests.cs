using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What the store-backed bridge root and its children allocate. The root's egglog-compatible API pays nothing per
    /// staged insert beyond what egglog's own paths pay, and a child costs in proportion to what it touches, not to
    /// what the e-graph defines or to the size of its root. Allocation is measured on the calling thread; nothing is
    /// timed.
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class BridgeCostTests
    {

        /// <summary>
        /// Runs <paramref name="body"/> to warm it, then returns the bytes one call allocates on this thread: the least
        /// of up to three windows of <paramref name="runs"/> calls, so a pool trim on a loaded machine does not count.
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
        /// Takes, and drops, every array that the shared <see cref="ArrayPool{T}"/> of <see cref="uint"/> holds for the
        /// calling thread in the buckets a row buffer of up to <paramref name="cells"/> cells grows through: each pooled
        /// length (<see cref="PooledArray.IsPoolable{T}"/>) up to <paramref name="cells"/> rounded up to a power of two.
        /// A bucket is empty once a rental from it allocates, which it does only when neither the thread's slot nor any
        /// core's stack held an array of that length.
        /// </summary>
        static void DrainRowPool(int cells)
        {
            var top = BitOperations.RoundUpToPowerOf2((uint)cells);
            for (var length = 2 * PooledArray.MaxUnpooledLength; length <= top && PooledArray.IsPoolable<uint>(length); length *= 2)
            {
                long before;
                do
                {
                    before = GC.GetAllocatedBytesForCurrentThread();
                    ArrayPool<uint>.Shared.Rent(length);
                }
                while (GC.GetAllocatedBytesForCurrentThread() == before);
            }
        }

        /// <summary>
        /// Returns the bytes one call of <paramref name="body"/> allocates on this thread over one window of
        /// <paramref name="runs"/> calls that starts from a settled row pool: the pool is drained
        /// (<see cref="DrainRowPool"/>), then one call puts back what a call returns to it. Each call in the window
        /// then finds in the pool exactly what the call before it returned, whatever earlier code left there, so the
        /// count is what a warm call of <paramref name="body"/> itself allocates.
        /// </summary>
        static long SettledWindow(Action body, int cells, int runs)
        {
            DrainRowPool(cells);
            body();
            return Window(body, runs);
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
        /// The egglog-compatible <see cref="EGraph.AddValues"/> at the root allocates what staging the same rows
        /// directly into an execution state and flushing allocates (egglog's <c>add_values</c>), within a quarter:
        /// nothing per row beyond the staged copy the merge needs.
        /// </summary>
        /// <remarks>
        /// The root stages each row straight into a database buffer (<see cref="EGraphContext.AddValuesAtRoot"/>).
        /// Copying every row into a temporary list first would cost about 33 bytes per row (49.8 KB for 1000 rows
        /// against 16.8 KB for direct staging).
        /// <para>
        /// Both paths grow one staging buffer through pooled arrays of 512 to 4096 cells and return only the last, so
        /// arrays of those lengths that earlier code left in the shared pool would serve a few calls' growth for free,
        /// and whichever path ran first would look cheaper. Each window therefore starts from a drained pool
        /// (<see cref="SettledWindow"/>), and the two paths are compared window against window. A pair that misses the
        /// bound is measured again, up to ten times, because something outside the calls can still move one window: a
        /// gen-2 collection trimming the pool adds bytes to it, an array another thread returns to the pool takes some
        /// away. A path that allocates per row misses the bound in every pair.
        /// </para>
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

            for (var i = 0; i < 5; i++)
            {
                Direct();
                ViaAddValues();
            }

            var cells = rows.Length * math.TableColumns;
            var pairs = new List<string>();
            var held = false;
            for (var round = 0; round < 10 && held == false; round++)
            {
                var direct = SettledWindow(Direct, cells, 20);
                var viaAddValues = SettledWindow(ViaAddValues, cells, 20);
                pairs.Add($"{viaAddValues} against {direct}");
                held = viaAddValues <= direct + direct / 4 + 1024;
            }

            Assert.True(held, $"AddValues of {rows.Length} rows allocated more than a quarter over staging them directly in every window (bytes per call, AddValues against direct): {string.Join(", ", pairs)}.");
        }

        /// <summary>
        /// After a bulk load through the egglog-compatible <see cref="EGraph.AddValues"/>, the root keeps no staging
        /// storage proportional to the batch: the rows live in the table, and nothing else of the batch is retained for
        /// the e-graph's lifetime.
        /// </summary>
        /// <remarks>
        /// The root's <see cref="HostWrites"/> releases arenas larger than its retention limits when a flush clears
        /// them. Arenas kept across flushes would leave about 6.5 MB of staging arrays on the root after a 200,000-row
        /// <c>AddValues</c> (19.4 MB against 12.8 MB retained in a Debug build, measured with
        /// <c>GC.GetTotalMemory</c>). Read through reflection: the arenas are private.
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
        /// A child's first rule run allocates the same whether the e-graph defines 1 rule or 4001: the per-rule
        /// progress a child keeps grows with the rules it runs, not with every rule ever defined, so a child costs in
        /// proportion to what it touches.
        /// </summary>
        /// <remarks>
        /// The progress is a map sized by the rules the context ran. An array sized to <c>RuleTable.Capacity</c> at the
        /// first commit would cost 16 bytes per defined rule (about 64 KB per child here), although the child runs
        /// rule 0 only.
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
        /// watermark it inherits, read a size and be disposed does not grow with the root: the same with 10 root edges
        /// as with 20,000.
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
        /// Guard: the root's reads allocate nothing once warm, through the egglog-compatible API and through the root
        /// context.
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
        /// context form over the root: per query the watch reads one version per slot, and per node nothing.
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
