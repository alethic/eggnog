using System;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review test for M9.3 (soundness lens): <see cref="Store.AddTable"/> reads the root's frozen flag before it enters
    /// the root's domain, so a root freeze that completes on another thread between the read and the enter leaves a
    /// table added to a frozen root unfrozen and unsealed (X43, §4.3 step 4).
    /// </summary>
    public class AddTableFreezeRaceReviewTests
    {

        [Fact]
        public void ATableAddedWhileTheRootFreezesIsFrozenOrRefused()
        {
            var iterations = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_ADDTABLE_RACE"), out var n) && n > 0 ? n : 5000;
            var bad = 0;
            for (var i = 0; i < iterations && bad == 0; i++)
            {
                var store = new Store(ParallelHeuristics.Serial);
                store.AddTable(MaxFunction(1), name: "f");
                var root = store.Root;
                using var start = new Barrier(2);
                TableId? added = null;
                var freezer = Task.Run(() =>
                {
                    start.SignalAndWait();
                    try
                    {
                        root.Freeze();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                });
                var adder = Task.Run(() =>
                {
                    start.SignalAndWait();
                    try
                    {
                        added = store.AddTable(MaxFunction(1), name: "g");
                    }
                    catch (InvalidOperationException)
                    {
                    }
                });

                Task.WaitAll(freezer, adder);
                if (root.IsFrozen && added is { } g && store.Schema[g]!.Root.IsShared == false)
                {
                    bad++;
                    Console.WriteLine($"iteration {i}: g={g} rootDbFrozen={root.Database.IsFrozen} fShared={store.Schema[new TableId(1)]!.Root.IsShared} gInRootDb={root.Database.TryGetTableInfo(g, out _)} rootViewFrozen={store.Schema.RootView(g).Layers[0]!.IsFrozen}");
                }
            }

            Assert.Equal(0, bad);
        }

    }

}
