using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for <see cref="ParallelVecWriter{T}"/> that expose defects in the port.
    /// </summary>
    public sealed class ParallelVecWriterReviewTests
    {

        [Fact]
        public void NegativeCountIsRejectedWithoutCorruptingReservations()
        {
            // Rust's ExactSizeIterator::len is a usize, so it can never be negative. IReadOnlyCollection<T>.Count can
            // be. The port promises to stop at the reserved count and leave other writers' ranges alone, but a
            // negative Count moves the logical length backwards (Interlocked.Add with a negative value), and then the
            // loop writes every yielded item past its "reservation".
            var v = new ParallelVecWriter<int>(new int[8], 0);
            Assert.Equal(0, v.WriteSlice(new[] { 1, 2 }));

            Assert.ThrowsAny<Exception>(() => v.WriteContents(new ParallelVecWriterLyingCollection(-1, 7, 8, 9)));

            // A rejected write must not shrink the logical length: the next write must not overlap [0, 2).
            var next = v.WriteSlice(new[] { 5 });
            Assert.True(next >= 2, $"the next write landed at {next}, inside the earlier write's range [0, 2)");

            var result = v.Finish(out var count);
            Assert.True(count >= 3, $"the logical length shrank to {count}");
            Assert.Equal(1, result[0]);
            Assert.Equal(2, result[1]);
        }

        [Fact]
        public void NegativeCountNeverWritesOutsideAnyReservation()
        {
            // Slots past the logical length must be untouched by a rejected write. (With an empty writer the negative
            // end trips the Array.MaxLength check first, so start from one written element.)
            var v = new ParallelVecWriter<int>(new int[8], 0);
            Assert.Equal(0, v.WriteSlice(new[] { 1 }));
            Assert.ThrowsAny<Exception>(() => v.WriteContents(new ParallelVecWriterLyingCollection(-1, 7, 8, 9)));
            using var access = v.ReadAccess();
            Assert.Equal(1, access.Buffer[0]);
            for (var i = 1; i < access.Buffer.Length; i++)
                Assert.True(access.Buffer[i] == 0, $"slot {i} past every reservation was written ({access.Buffer[i]})");
        }

    }

}
