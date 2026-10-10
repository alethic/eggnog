using System;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for <see cref="ParallelRowBufferReadHandle"/>. The handle is a copyable, mutable, disposable struct
    /// that owns one count in the writer's read-section counter; disposing a copy closes the section a second time.
    /// egglog's <c>ReadHandle</c> is a borrow-scoped guard that cannot be duplicated.
    /// </summary>
    public class ParallelRowBufferWriterReviewTests
    {

        /// <summary>
        /// Disposes its own copy of the handle, the way a helper that takes the handle by value and wraps it in
        /// <c>using</c> would.
        /// </summary>
        static void InspectAndClose(ParallelRowBufferReadHandle handle)
        {
            using (handle)
                Assert.False(handle.IsStale(0));
        }

        /// <summary>
        /// After a by-value copy is disposed, a resize no longer waits for a read handle that is still open on another
        /// thread, so that handle keeps reading (and marking stale) storage that was released to the shared pool.
        /// </summary>
        [Fact]
        public async Task DisposingACopiedHandleDoesNotLetAResizeRunUnderAnotherOpenHandle()
        {
            var rows = new RowBuffer(1);
            rows.AddRow(new[] { 1u });
            var writer = rows.ParallelWriter();

            var mine = writer.ReadHandle();
            var other = writer.ReadHandle();
            InspectAndClose(mine);

            // `mine` was handed to the helper by value; its owner still disposes it as usual.
            mine.Dispose();

            // `other` is still open, so a resize must wait for it.
            var big = new RowBuffer(1);
            for (uint i = 0; i < 5000; i++)
                big.AddRow(new[] { i });
            var cancellation = TestContext.Current.CancellationToken;
            var append = Task.Run(() => writer.AppendContents(big), cancellation);
            var first = await Task.WhenAny(append, Task.Delay(500, cancellation));
            var resizedWhileOpen = ReferenceEquals(first, append);

            // Only close `other` when the resize correctly waited for it; otherwise the counter is already at zero and
            // closing it would drive it negative.
            if (resizedWhileOpen == false)
            {
                other.Dispose();
                await append;
            }

            Assert.False(resizedWhileOpen, "A resize replaced the storage while a read handle was still open.");
        }

    }

}
