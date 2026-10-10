using System;
using System.Buffers;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for the row buffer enumerators. Their remarks promise that growing the buffer while enumerating
    /// "leaves it reading the old storage", but growth hands pooled storage (more than 256 cells) straight back to
    /// <see cref="ArrayPool{T}.Shared"/>, so the enumerator keeps reading, and through its writable
    /// <c>Current</c> writing, an array that another component may already have rented.
    /// </summary>
    public class RowBufferEnumeratorReviewTests
    {

        [Fact]
        public void RowEnumeratorKeepsReadingItsOldStorageAfterGrowth()
        {
            var rows = new RowBuffer(1);
            for (uint i = 0; i < 512; i++)
                rows.AddRow(new[] { i });
            Assert.Equal(512, rows.Data.Length);

            var e = rows.EnumerateRows();
            Assert.True(e.MoveNext());

            // Grows to 1024 cells and releases the full 512-cell array to the shared pool.
            rows.AddRow(new[] { 999u });

            // Any other code on this thread now rents that array and fills it.
            var foreign = ArrayPool<uint>.Shared.Rent(512);
            Array.Fill(foreign, 0xDEADu);

            Assert.True(e.MoveNext());
            var seen = e.Current[0];
            ArrayPool<uint>.Shared.Return(foreign);

            // The documented behaviour: the enumerator still reads the old storage, whose row 1 holds 1.
            Assert.Equal(1u, seen);
        }

        [Fact]
        public void TaggedNonStaleEnumeratorWritesIntoAnArraySomeoneElseOwnsAfterGrowth()
        {
            var rows = new TaggedRowBuffer(1);
            for (var i = 0; i < 256; i++)
                rows.AddRow(i, new[] { (uint)i });
            Assert.Equal(512, rows.Data.Length);

            var e = rows.EnumerateNonStale();
            Assert.True(e.MoveNext());

            // Grows and releases the 512-cell array to the shared pool.
            rows.AddRow(256, new[] { 256u });
            var foreign = ArrayPool<uint>.Shared.Rent(512);
            Array.Clear(foreign);

            // An in-place rewrite through the enumerator (egglog's non_stale_mut pattern) lands in the foreign array.
            e.Current.Values[0] = 42;
            var corrupted = foreign[0];
            ArrayPool<uint>.Shared.Return(foreign);

            Assert.Equal(0u, corrupted);
        }

    }

}
