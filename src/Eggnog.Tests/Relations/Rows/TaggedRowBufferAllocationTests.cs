using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="TaggedRowBuffer.CreateInline"/> allocates no more than the ordinary constructor. egglog's
    /// <c>new_inline</c> (row_buffer/mod.rs:410-424) exists so that free join's per-frame leaf scans
    /// (free_join/execute.rs:1394, 1586) allocate nothing on the heap; those scans often come back empty and return
    /// immediately.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class TaggedRowBufferAllocationTests
    {

        [Fact]
        public void CreateInlineAllocatesNoMoreThanTheOrdinaryConstructor()
        {
            // Warm up so that JIT and type loading do not count.
            GC.KeepAlive(TaggedRowBuffer.CreateInline(2));
            GC.KeepAlive(new TaggedRowBuffer(2));

            var start = GC.GetAllocatedBytesForCurrentThread();
            var ordinary = new TaggedRowBuffer(2);
            var middle = GC.GetAllocatedBytesForCurrentThread();
            var inline = TaggedRowBuffer.CreateInline(2);
            var end = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(ordinary);
            GC.KeepAlive(inline);

            var ordinaryBytes = middle - start;
            var inlineBytes = end - middle;
            Assert.True(inlineBytes <= ordinaryBytes, $"CreateInline allocated {inlineBytes} bytes; the ordinary constructor {ordinaryBytes}.");
        }

    }

}
