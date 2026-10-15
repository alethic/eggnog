using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for layer creation from the tasks of a child's rule run: clones of one execution state, as
    /// <see cref="ActionBatchWork"/> makes them, stage into the same untouched tables on many threads during a search.
    /// Exactly one layer per table is created and published but not adopted, a concurrent reader of the catalog sees
    /// each layer appear once and never change, and no view or view generation changes during the search; the next
    /// merge adopts the layers and merges every write.
    /// </summary>
    public class LayerCreationConcurrencyTests
    {

        [Fact]
        public void ConcurrentClonesCreateOneLayerPerTableAndLoseNoWrite()
        {
            const int Threads = 8;
            const int RowsPerTable = 200;
            for (var trial = 0; trial < 4; trial++)
                Run(Threads, RowsPerTable, trial);
        }

        static void Run(int threads, int rowsPerTable, int trial)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var tables = Enumerable.Range(0, 6).Select(i => store.AddTable(MaxFunction(1), name: $"t{i}")).ToArray();
            var root = store.Root;
            var ids = Enumerable.Range(0, threads * rowsPerTable).Select(_ => root.NewId()).ToArray();
            Insert(root, tables[0], ids[0], 100, 0);
            root.Freeze();
            var child = root.CreateChild();
            var layering = child.Tables!;
            var db = child.Database;
            var generation = layering.ViewGeneration;
            var viewBefore = child.GetView(tables[0]);

            // The search: rule tasks stage through clones of the run's root state.
            layering.InSearch = true;
            var state = db.CreateExecutionState(store.ReadClock());
            var clones = Enumerable.Range(0, threads).Select(_ => state.Clone()).ToArray();
            using var start = new Barrier(threads + 1);
            var stop = 0;
            var seen = new Table?[tables.Length];
            var torn = 0;
            var reader = Task.Run(() =>
            {
                start.SignalAndWait();
                while (Volatile.Read(ref stop) == 0)
                {
                    for (var i = 0; i < tables.Length; i++)
                    {
                        if (db.Catalog.TryGetTable(tables[i], out var found))
                        {
                            if (seen[i] is null)
                                seen[i] = found;
                            else if (ReferenceEquals(seen[i], found) == false)
                                Interlocked.Increment(ref torn);
                        }
                        else if (seen[i] is not null)
                        {
                            Interlocked.Increment(ref torn);
                        }
                    }
                }
            });

            var writers = Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
            {
                var clone = clones[thread];
                var order = tables.OrderBy(_ => (thread * 7919 + trial) % 13).ToArray();
                start.SignalAndWait();
                for (var r = 0; r < rowsPerTable; r++)
                {
                    foreach (var table in order)
                        clone.StageInsert(table, [ids[thread * rowsPerTable + r], (uint)thread, clone.Timestamp]);
                }

                clone.Flush();
            })).ToArray();

            Task.WaitAll(writers);
            Volatile.Write(ref stop, 1);
            reader.Wait();
            Assert.Equal(0, torn);

            // One layer per table, published but not adopted during the search; no view and no generation changed.
            foreach (var table in tables)
            {
                var layer = layering.TryGetLayer(table);
                Assert.NotNull(layer);
                Assert.False(layer!.IsAdopted);
                Assert.Same(layer.Own, db.Catalog.GetForStaging(table));
                Assert.False(db.TryGetTableInfo(table, out _));
            }

            Assert.Equal(tables.Length, layering.Statistics.LayersCreated);
            Assert.Equal(generation, layering.ViewGeneration);
            Assert.Same(viewBefore, layering.GetView(tables[0]));

            // The merge adopts the layers first, then merges every row; its epilogue drops the views that changed.
            layering.InSearch = false;
            Assert.True(db.MergeAll(state.Timestamp));
            foreach (var table in tables)
            {
                var layer = layering.TryGetLayer(table)!;
                Assert.True(layer.IsAdopted);
                Assert.Same(layer.Info, db.GetTableInfo(table));

                // The root's t0(ids[0]) = 100 is larger than thread 0's value for that key, so that one row merges to
                // nothing.
                Assert.Equal(threads * rowsPerTable - (table == tables[0] ? 1 : 0), layer.Own.Count);
            }

            Assert.True(layering.ViewGeneration > generation);
            Assert.NotSame(viewBefore, child.GetView(tables[0]));
            Assert.Equal(threads * rowsPerTable, child.GetView(tables[1]).VisibleCount);
            LookupPaths.Check(child);
        }

        /// <summary>
        /// A first write that finds no layer and then waits for the layering's lock, while another writer creates the
        /// layer, takes that layer rather than a second one.
        /// </summary>
        [Fact]
        public void AFirstWriteThatWaitsForTheLockTakesTheLayerCreatedMeanwhile()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var table = store.AddTable(MaxFunction(1), name: "t");
            store.Root.Freeze();
            var child = store.Root.CreateChild();
            var layering = child.Tables!;
            var layerLock = typeof(ContextTables).GetField("layerLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(layering)!;
            using var starting = new ManualResetEventSlim();

            TableLayer created;
            Task<TableLayer> waiting;
            lock (layerLock)
            {
                waiting = Task.Factory.StartNew(() =>
                {
                    starting.Set();
                    return layering.GetOrCreateLayer(table);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(starting.Wait(TimeSpan.FromSeconds(30)));
                Assert.False(waiting.Wait(TimeSpan.FromMilliseconds(100)));
                Assert.Null(layering.TryGetLayer(table));
                created = layering.GetOrCreateLayer(table);
            }

            Assert.True(waiting.Wait(TimeSpan.FromSeconds(30)));
            Assert.Same(created, waiting.Result);
            Assert.Same(created, layering.TryGetLayer(table));
            Assert.Equal(1, layering.Statistics.LayersCreated);
        }

    }

}
