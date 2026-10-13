using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Checks that the per-probe and per-row operations allocate nothing on the managed heap once the buffer pool is
    /// warm: dense subsets never touch the heap, and sparse ones recycle pooled buffers.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class SubsetAllocationTests
    {

        /// <summary>
        /// Gets the bytes <paramref name="body"/> allocates on this thread over 200 calls once warm, as the smallest of up
        /// to five rounds. The shared array pool trims its cached arrays after a full collection when the machine is
        /// under memory pressure, so one round can pay to refill it; an allocation in the body itself shows in every
        /// round (at least 200 objects), so taking the smallest round keeps the check exact for the operations measured.
        /// </summary>
        static long Run(Action body)
        {
            // Warm up so JIT tiering, static constructors and the pool's thread-local buckets are settled.
            for (var i = 0; i < 3; i++)
                body();

            var least = long.MaxValue;
            for (var round = 0; round < 5 && least > 0; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 200; i++)
                    body();

                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return least;
        }

        [Fact]
        public void DenseOperationsDoNotAllocate()
        {
            var allocated = Run(static () =>
            {
                var s = Subset.Dense(0, 1000);
                s.Intersect(SubsetRef.Dense(new OffsetRange(100, 900)));
                s.AddRowSorted(900);
                var visitor = new SummingVisitor();
                s.ForEach(ref visitor);
                var position = 0;
                while (position >= 0)
                    position = s.AsRef().IterBounded(position, position + 64, ref visitor);

                foreach (var row in s)
                    visitor.Sum += row;

                s.AsRef().TryIntersectDense(new OffsetRange(50, 150), out _);
                var copy = s.Clone();
                copy.Dispose();
                s.Dispose();
            });

            Assert.Equal(0, allocated);
        }

        [Fact]
        public void SparseOperationsRecyclePooledBuffers()
        {
            var a = Enumerable.Range(0, 2000).Select(x => x * 3).ToArray();
            var b = Enumerable.Range(0, 3000).Select(x => x * 2).ToArray();
            var small = Enumerable.Range(0, 40).Select(x => x * 97).ToArray();

            var allocated = Run(() =>
            {
                var s = SubsetRef.Sparse(a, 0, a.Length).ToOwned();
                s.Intersect(SubsetRef.Sparse(b, 0, b.Length));
                s.Intersect(SubsetRef.Sparse(small, 0, small.Length));
                s.Intersect(SubsetRef.Dense(new OffsetRange(10, 5000)));
                var filter = new ModulusFilter(2);
                s.Retain(ref filter);
                var visitor = new SummingVisitor();
                s.ForEach(ref visitor);
                s.AsRef().IterBounded(0, 8, ref visitor);
                s.AsRef().TryIntersectDense(new OffsetRange(0, 100), out _);

                var d = Subset.Dense(0, 4000);
                d.Intersect(SubsetRef.Sparse(b, 0, b.Length));
                d.AddRowSorted(10000);

                var r = Subset.Dense(0, 500);
                var keepEven = new ModulusFilter(2);
                r.Retain(ref keepEven);

                var v = new SortedOffsetVector();
                v.FillFromDense(new OffsetRange(0, 100));
                v.Push(200);
                var fromVector = v.ToSubset();

                fromVector.Dispose();
                r.Dispose();
                d.Dispose();
                s.Dispose();
            });

            Assert.Equal(0, allocated);
        }

    }

}
