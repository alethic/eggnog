using System;
using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ArbitraryRowBuffer"/>.
    /// </summary>
    public class ArbitraryRowBufferTests
    {

        static uint[] Row(params uint[] cells) => cells;

        static List<uint[]> Rows(ArbitraryRowBuffer buffer)
        {
            var list = new List<uint[]>();
            foreach (var row in buffer.EnumerateRows())
                list.Add(row.ToArray());

            return list;
        }

        [Fact]
        public void ArityZeroCountsEmptyRows()
        {
            var buffer = new ArbitraryRowBuffer(0);
            Assert.True(buffer.IsEmpty);
            Assert.Null(buffer.Buffer);

            buffer.AddRow(ReadOnlySpan<uint>.Empty);
            buffer.AddRow(Array.Empty<uint>(), 0);
            buffer.AddRow(ReadOnlySpan<uint>.Empty);

            Assert.Equal(3, buffer.Count);
            var rows = Rows(buffer);
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row => Assert.Empty(row));
        }

        [Fact]
        public void ArityZeroRejectsCells()
        {
            var buffer = new ArbitraryRowBuffer(0);

            Assert.Throws<ArgumentException>(() => buffer.AddRow(Row(1)));
            Assert.Equal(0, buffer.Count);
        }

        [Fact]
        public void PositiveArityStoresRows()
        {
            var buffer = new ArbitraryRowBuffer(2);
            buffer.AddRow(Row(1, 2));
            buffer.AddRow(Row(9, 3, 4), 1);

            Assert.Equal(2, buffer.Arity);
            Assert.Equal(2, buffer.Count);
            Assert.NotNull(buffer.Buffer);
            Assert.Equal(new List<uint[]> { Row(1, 2), Row(3, 4) }, Rows(buffer));
            Assert.Throws<ArgumentException>(() => buffer.AddRow(Row(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.AddRow(Row(1, 2), 1));
        }

        [Fact]
        public void NegativeArityIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ArbitraryRowBuffer(-1));
        }

        [Fact]
        public void CloneIsIndependent()
        {
            var empty = new ArbitraryRowBuffer(0);
            empty.AddRow(ReadOnlySpan<uint>.Empty);
            var emptyCopy = empty.Clone();
            empty.AddRow(ReadOnlySpan<uint>.Empty);
            Assert.Equal(1, emptyCopy.Count);

            var full = new ArbitraryRowBuffer(1);
            full.AddRow(Row(5));
            var fullCopy = full.Clone();
            full.AddRow(Row(6));
            Assert.Equal(1, fullCopy.Count);
            Assert.Equal(new List<uint[]> { Row(5) }, Rows(fullCopy));
        }

        [Fact]
        public void DisposeEmpties()
        {
            var empty = new ArbitraryRowBuffer(0);
            empty.AddRow(ReadOnlySpan<uint>.Empty);
            empty.Dispose();
            Assert.Equal(0, empty.Count);

            var full = new ArbitraryRowBuffer(3);
            full.AddRow(Row(1, 2, 3));
            full.Dispose();
            full.Dispose();
            Assert.Equal(0, full.Count);
        }

    }

}
