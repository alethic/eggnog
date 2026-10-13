using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ParallelRowBufferWriter"/> and <see cref="ParallelRowBufferReadHandle"/>, including a port
    /// of egglog's <c>parallel_row_buf_append_contents</c> and concurrent stress tests that force resizes while
    /// other threads copy, read and mark rows stale.
    /// </summary>
    public class ParallelRowBufferWriterTests
    {

        const uint Stale = Value.StaleRep;

        static uint[] Row(params uint[] cells) => cells;

        /// <summary>
        /// Port of <c>parallel_row_buf_append_contents</c>.
        /// </summary>
        [Fact]
        public void AppendContents()
        {
            var rows = new RowBuffer(2);
            rows.AddRow(Row(0, 1));
            var writer = rows.ParallelWriter();

            var extra = new RowBuffer(2);
            extra.AddRow(Row(2, 3));
            extra.AddRow(Row(4, 5));
            var start = writer.AppendContents(extra);
            Assert.Equal(1, start);

            var finished = writer.Finish();
            Assert.Equal(3, finished.Count);
            Assert.Equal(Row(0, 1), finished.GetRow(0).ToArray());
            Assert.Equal(Row(2, 3), finished.GetRow(1).ToArray());
            Assert.Equal(Row(4, 5), finished.GetRow(2).ToArray());
        }

        [Fact]
        public void TheSourceBufferIsLeftEmptyAndUsable()
        {
            var rows = new RowBuffer(1);
            rows.AddRow(Row(7));

            var writer = rows.ParallelWriter();

            Assert.Equal(0, rows.Count);
            Assert.Equal(1, writer.Count);
            Assert.Equal(1, writer.Arity);
            rows.AddRow(Row(8));
            Assert.Equal(Row(7), writer.Finish().GetRow(0).ToArray());
        }

        [Fact]
        public void ReservedCapacityMeansNoResize()
        {
            var rows = new RowBuffer(3);
            rows.AddRow(Row(1, 2, 3));
            rows.Reserve(1000);
            var data = rows.Data;
            var writer = rows.ParallelWriter();

            Parallel.For(0, 10, i =>
            {
                var batch = new RowBuffer(3);
                for (var j = 0; j < 100; j++)
                    batch.AddRow(Row((uint)i, (uint)j, 0));

                writer.AppendContents(batch);
            });

            var finished = writer.Finish();
            Assert.Same(data, finished.Data);
            Assert.Equal(1001, finished.Count);
        }

        [Fact]
        public void EmptyAppendReturnsTheCurrentEnd()
        {
            var rows = new RowBuffer(2);
            rows.AddRow(Row(1, 1));
            rows.AddRow(Row(2, 2));
            var writer = rows.ParallelWriter();

            Assert.Equal(2, writer.AppendContents(new RowBuffer(2)));
            Assert.Equal(2, writer.Finish().Count);
        }

        [Fact]
        public void MisuseIsRejected()
        {
            var writer = new RowBuffer(2).ParallelWriter();

            Assert.Throws<ArgumentException>(() => writer.AppendContents(new RowBuffer(3)));
            Assert.Throws<ArgumentNullException>(() => writer.AppendContents(null!));

            writer.Finish();
            Assert.Throws<InvalidOperationException>(() => writer.Finish());
            Assert.Throws<InvalidOperationException>(() => writer.AppendContents(new RowBuffer(2)));
            Assert.Throws<InvalidOperationException>(() => writer.ReadHandle());
        }

        [Fact]
        public void ReadHandleReadsAndMarksRows()
        {
            var rows = new RowBuffer(2);
            rows.AddRow(Row(1, 10));
            rows.AddRow(Row(2, 20));
            var writer = rows.ParallelWriter();
            var batch = new RowBuffer(2);
            batch.AddRow(Row(3, 30));
            var start = writer.AppendContents(batch);

            using (var handle = writer.ReadHandle())
            {
                Assert.Equal(2, handle.Arity);
                Assert.Equal(Row(2, 20), handle.GetRow(1).ToArray());
                Assert.Equal(Row(3, 30), handle.GetRowUnchecked(start).ToArray());
                Assert.False(handle.IsStale(0));
                Assert.False(handle.SetStaleShared(0));
                Assert.True(handle.SetStaleShared(0));
                Assert.Equal(Stale, handle.Data[0]);
            }

            var finished = writer.Finish();
            Assert.True(finished.IsStale(0));
            Assert.False(finished.IsStale(2));
        }

        /// <summary>
        /// Many threads append batches of identifiable rows into a writer that starts tiny, so resizes happen while
        /// other threads copy and read. Each thread reads its rows back through a read handle and marks every third
        /// one stale. Afterwards every batch must sit at the offset its append returned, the batches must tile the
        /// buffer exactly, and the stale marks must have survived every resize.
        /// </summary>
        [Theory]
        [InlineData(1, 2)]
        [InlineData(2, 4)]
        [InlineData(3, 8)]
        [InlineData(4, 16)]
        public void ConcurrentAppendsLandAtTheirOffsets(int seed, int threads)
        {
            const int arity = 3;
            const int initialRows = 5;
            var rows = new RowBuffer(arity);
            for (uint i = 0; i < initialRows; i++)
                rows.AddRow(Row(1000, i, 0));

            var writer = rows.ParallelWriter();
            var appended = new ConcurrentBag<(int Thread, int Batch, int Start, int Count)>();

            var options = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = TestContext.Current.CancellationToken };
            Parallel.For(0, threads, options, t =>
            {
                var rng = new Random(seed * 100 + t);
                var staged = new RowBuffer(arity);
                for (var b = 0; b < 200; b++)
                {
                    staged.Clear();
                    var n = rng.Next(0, 50);
                    for (var i = 0; i < n; i++)
                        staged.AddRow(Row((uint)t, (uint)b, (uint)i));

                    var start = writer.AppendContents(staged);
                    using (var handle = writer.ReadHandle())
                    {
                        for (var i = 0; i < n; i++)
                        {
                            Assert.Equal(Row((uint)t, (uint)b, (uint)i), handle.GetRow(start + i).ToArray());
                            if (i % 3 == 0)
                                Assert.False(handle.SetStaleShared(start + i));
                        }
                    }

                    appended.Add((t, b, start, n));
                }

                staged.Dispose();
            });

            var finished = writer.Finish();
            var total = initialRows + appended.Sum(x => x.Count);
            Assert.Equal(total, finished.Count);

            for (uint i = 0; i < initialRows; i++)
                Assert.Equal(Row(1000, i, 0), finished.GetRow((int)i).ToArray());

            var next = initialRows;
            foreach (var batch in appended.OrderBy(x => x.Start).ThenBy(x => x.Count))
            {
                Assert.Equal(next, batch.Start);
                for (var i = 0; i < batch.Count; i++)
                {
                    var expected = Row(i % 3 == 0 ? Stale : (uint)batch.Thread, (uint)batch.Batch, (uint)i);
                    Assert.Equal(expected, finished.GetRow(batch.Start + i).ToArray());
                }

                next += batch.Count;
            }

            Assert.Equal(total, next);
            finished.Dispose();
        }

        /// <summary>
        /// A thread holding a read handle blocks a resize until it lets go; the resize then completes and the
        /// appending thread's rows are intact.
        /// </summary>
        [Fact]
        public async Task ResizeWaitsForOpenReadHandles()
        {
            var cancellation = TestContext.Current.CancellationToken;
            var rows = new RowBuffer(1);
            rows.AddRow(Row(1));
            var writer = rows.ParallelWriter();

            using var holding = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                using var handle = writer.ReadHandle();
                holding.Set();
                release.Wait(cancellation);
                Assert.Equal(Row(1), handle.GetRow(0).ToArray());
            }, cancellation);

            holding.Wait(cancellation);
            var big = new RowBuffer(1);
            for (uint i = 0; i < 5000; i++)
                big.AddRow(Row(i + 2));

            var appender = Task.Run(() => writer.AppendContents(big), cancellation);
            var first = await Task.WhenAny(appender, Task.Delay(200, cancellation));
            Assert.NotSame(appender, first);

            release.Set();
            await reader;
            Assert.Equal(1, await appender);

            var finished = writer.Finish();
            Assert.Equal(5001, finished.Count);
            Assert.Equal(Row(5001), finished.GetRow(5000).ToArray());
        }

        /// <summary>
        /// Disposing a handle again, or from two threads at once, closes its read section once: a later resize does not
        /// wait for a section nobody holds, and does not run while one is still open.
        /// </summary>
        [Fact]
        public async Task DisposingAHandleRepeatedlyClosesItsSectionOnce()
        {
            var cancellation = TestContext.Current.CancellationToken;
            var rows = new RowBuffer(1);
            rows.AddRow(Row(1));
            var writer = rows.ParallelWriter();

            var once = writer.ReadHandle();
            once.Dispose();
            once.Dispose();

            var raced = writer.ReadHandle();
            Parallel.Invoke(new ParallelOptions { CancellationToken = cancellation }, raced.Dispose, raced.Dispose, raced.Dispose);

            var open = writer.ReadHandle();
            var big = new RowBuffer(1);
            for (uint i = 0; i < 5000; i++)
                big.AddRow(Row(i + 2));

            var appender = Task.Run(() => writer.AppendContents(big), cancellation);
            var first = await Task.WhenAny(appender, Task.Delay(200, cancellation));
            Assert.NotSame(appender, first);

            open.Dispose();
            Assert.Equal(1, await appender.WaitAsync(TimeSpan.FromSeconds(30), cancellation));
            Assert.Equal(5001, writer.Finish().Count);
        }

        /// <summary>
        /// A resize leaves the old storage to the garbage collector, as every growth does, so nobody can rent it while
        /// something still refers to it.
        /// </summary>
        [Fact]
        public void ResizeDoesNotRecycleTheOldStorage()
        {
            var rows = new RowBuffer(1);
            for (uint i = 0; i < 512; i++)
                rows.AddRow(Row(i));
            var old = rows.Data;
            Assert.Equal(512, old.Length);
            var writer = rows.ParallelWriter();

            var batch = new RowBuffer(1);
            batch.AddRow(Row(512));
            Assert.Equal(512, writer.AppendContents(batch));

            var rented = ArrayPool<uint>.Shared.Rent(512);
            Assert.NotSame(old, rented);
            ArrayPool<uint>.Shared.Return(rented);

            var finished = writer.Finish();
            Assert.NotSame(old, finished.Data);
            Assert.Equal(513, finished.Count);
            Assert.Equal(Row(511), old.AsSpan(511, 1).ToArray());
        }

        /// <summary>
        /// Disposes the handle it is given, the way a helper that takes the handle as a parameter and wraps it in
        /// <c>using</c> would. The handle is a class, so the helper's parameter and the caller's variable are the same
        /// handle.
        /// </summary>
        static void InspectAndClose(ParallelRowBufferReadHandle handle)
        {
            using (handle)
                Assert.False(handle.IsStale(0));
        }

        /// <summary>
        /// A helper that disposes a handle it was given, followed by the owner's own <c>Dispose</c>, closes the
        /// handle's read section once: a resize still waits for another read handle that is open on another thread,
        /// instead of replacing the storage that handle still reads (and marks stale).
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

            // The helper disposed `mine`; its owner still disposes it as usual.
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
