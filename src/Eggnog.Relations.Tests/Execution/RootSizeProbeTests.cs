using System;
using System.IO;
using System.Runtime.CompilerServices;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probes (root cost): sizes of the executor's per-node, per-prober and per-entry types, written to a report
    /// file so the same source can be compared between builds of the engine.
    /// </summary>
    public class RootSizeProbeTests
    {

        static readonly string Report = Environment.GetEnvironmentVariable("RC_OUT") ?? Path.Combine(AppContext.BaseDirectory, "rootcost.txt");

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
