using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="TaggedRowBuffer"/>, <see cref="TaggedRowSink"/> and <see cref="IRowSink"/>, including a
    /// port of egglog's <c>basic_tagged_row</c>.
    /// </summary>
    public class TaggedRowBufferTests
    {

        const uint Stale = Value.StaleRep;

        static uint[] Row(params uint[] cells) => cells;

        static List<(int RowId, uint[] Row)> All(TaggedRowBuffer buffer)
        {
            var list = new List<(int, uint[])>();
            foreach (var (rowId, row) in buffer.EnumerateRows())
                list.Add((rowId, row.ToArray()));

            return list;
        }

        static List<(int RowId, uint[] Row)> NonStale(TaggedRowBuffer buffer)
        {
            var list = new List<(int, uint[])>();
            foreach (var row in buffer.EnumerateNonStale())
                list.Add((row.RowId, row.Values.ToArray()));

            return list;
        }

        /// <summary>
        /// Copies the live rows of <paramref name="source"/> into a sink, the shape of a table scan.
        /// </summary>
        static void Scan<TSink>(RowBuffer source, ref TSink sink)
            where TSink : struct, IRowSink
        {
            var e = source.EnumerateNonStale();
            while (e.MoveNext())
                sink.AddRow(e.Row, e.Current);
        }

        /// <summary>
        /// Port of <c>basic_tagged_row</c>.
        /// </summary>
        [Fact]
        public void BasicTaggedRow()
        {
            var rows = new TaggedRowBuffer(2);
            var r1 = rows.AddRow(4, Row(0, 1));
            var r2 = rows.AddRow(7, Row(2, 3));

            Assert.Equal(Row(0, 1), rows.GetRow(r1, out var id1).ToArray());
            Assert.Equal(4, id1);
            Assert.Equal(Row(2, 3), rows.GetRow(r2, out var id2).ToArray());
            Assert.Equal(7, id2);

            var all = All(rows);
            Assert.Equal(2, all.Count);
            Assert.Equal(4, all[0].RowId);
            Assert.Equal(Row(0, 1), all[0].Row);
            Assert.Equal(7, all[1].RowId);
            Assert.Equal(Row(2, 3), all[1].Row);
        }

        [Fact]
        public void LayoutPutsTheTagLast()
        {
            var rows = new TaggedRowBuffer(3);
            rows.AddRow(11, Row(1, 2, 3));
            rows.AddRow(12, Row(4, 5, 6));

            Assert.Equal(3, rows.Arity);
            Assert.Equal(4, rows.Stride);
            Assert.Equal(Row(1, 2, 3, 11, 4, 5, 6, 12), rows.Data.AsSpan(0, 8).ToArray());
            Assert.Equal(4, rows.RowOffset(1));
            Assert.Equal(12, rows.GetRowId(1));
            Assert.Equal(Row(4, 5, 6), rows.GetRow(1).ToArray());
        }

        [Fact]
        public void NonStaleSkipsStaleRows()
        {
            var rows = new TaggedRowBuffer(2);
            for (uint i = 0; i < 6; i++)
                rows.AddRow((int)i + 100, Row(i, i));

            Assert.False(rows.SetStale(1));
            Assert.True(rows.SetStale(1));
            rows.SetStale(4);

            Assert.True(rows.IsStale(1));
            Assert.Equal(new[] { 100, 102, 103, 105 }, NonStale(rows).Select(x => x.RowId).ToArray());
            Assert.Equal(6, All(rows).Count);

            var e = rows.EnumerateNonStale();
            var ids = new List<int>();
            while (e.MoveNext())
                ids.Add(e.Row);
            Assert.Equal(new[] { 0, 2, 3, 5 }, ids.ToArray());
        }

        [Fact]
        public void CurrentIsEmptyOutsideTheRows()
        {
            var rows = new TaggedRowBuffer(2);
            rows.AddRow(9, Row(1, 2));

            var all = rows.EnumerateRows();
            Assert.True(all.Current.Values.IsEmpty);
            Assert.True(all.MoveNext());
            Assert.Equal(9, all.Current.RowId);
            Assert.Equal(Row(1, 2), all.Current.Values.ToArray());
            Assert.False(all.MoveNext());
            Assert.True(all.Current.Values.IsEmpty);
            Assert.Equal(0, all.Current.RowId);
            Assert.False(all.MoveNext());

            var live = rows.EnumerateNonStale();
            Assert.True(live.Current.Values.IsEmpty);
            Assert.True(live.MoveNext());
            Assert.Equal(9, live.Current.RowId);
            Assert.False(live.MoveNext());
            Assert.True(live.Current.Values.IsEmpty);
            Assert.False(live.MoveNext());
        }

        /// <summary>
        /// An enumerator started before a growth keeps reading the rows it started with, from storage that growth did
        /// not hand back to the shared pool.
        /// </summary>
        [Fact]
        public void EnumeratorKeepsItsRowsAcrossGrowth()
        {
            var rows = new TaggedRowBuffer(1);
            for (var i = 0; i < 256; i++)
                rows.AddRow(i + 1000, Row((uint)i));

            var e = rows.EnumerateRows();
            rows.AddRow(5000, Row(5000));

            var seen = 0;
            while (e.MoveNext())
            {
                Assert.Equal(e.Row + 1000, e.Current.RowId);
                Assert.Equal((uint)e.Row, e.Current.Values[0]);
                seen++;
            }

            Assert.Equal(256, seen);
            Assert.Equal(5000, rows.GetRowId(256));
        }

        [Fact]
        public void MutableRowsWriteThrough()
        {
            var rows = new TaggedRowBuffer(2);
            rows.AddRow(5, Row(1, 2));
            rows.AddRow(6, Row(3, 4));

            var row = rows.GetMutableRow(1, out var id);
            row[0] = 30;
            Assert.Equal(6, id);

            foreach (var (_, values) in rows.EnumerateNonStale())
                values[1] += 100;

            Assert.Equal(Row(1, 102), rows.GetRow(0).ToArray());
            Assert.Equal(Row(30, 104), rows.GetRow(1).ToArray());
            Assert.Equal(5, rows.GetRowId(0));
        }

        [Fact]
        public void ArityZeroStoresOnlyTags()
        {
            var rows = new TaggedRowBuffer(0);
            rows.AddRow(3, ReadOnlySpan<uint>.Empty);
            rows.AddRow(9, ReadOnlySpan<uint>.Empty);

            Assert.Equal(1, rows.Stride);
            Assert.Equal(2, rows.Count);
            Assert.Equal(9, rows.GetRowId(1));
            Assert.Equal(0, rows.GetRow(1).Length);
            Assert.Equal(new[] { 3, 9 }, All(rows).Select(x => x.RowId).ToArray());
        }

        [Fact]
        public void InvalidArgumentsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TaggedRowBuffer(-1));

            var rows = new TaggedRowBuffer(2);
            Assert.Throws<ArgumentException>(() => rows.AddRow(1, Row(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.GetRow(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.GetRowId(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.SetStale(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.AddRow(1, Row(1, 2), 1));
        }

        [Fact]
        public void InlineBufferAllocatesSixtyFourCellsOnItsFirstRow()
        {
            var rows = TaggedRowBuffer.CreateInline(3);
            Assert.Empty(rows.Data);

            rows.AddRow(0, Row(1, 2, 3));
            Assert.Equal(TaggedRowBuffer.InlineCells, rows.Data.Length);
            var data = rows.Data;

            for (var i = 1; i < 16; i++)
                rows.AddRow(i, Row(1, 2, 3));
            Assert.Same(data, rows.Data);

            rows.AddRow(16, Row(1, 2, 3));
            Assert.NotSame(data, rows.Data);
            Assert.Equal(16, rows.GetRowId(16));
            Assert.Equal(Row(1, 2, 3), rows.GetRow(0).ToArray());

            // Clearing keeps the storage, so a reused inline buffer allocates nothing more.
            data = rows.Data;
            rows.Clear();
            rows.AddRow(0, Row(4, 5, 6));
            Assert.Same(data, rows.Data);
        }

        [Fact]
        public void InlineBufferWithWideRowsAllocatesAtLeastOneRow()
        {
            var rows = TaggedRowBuffer.CreateInline(100);
            rows.AddRow(7, new uint[100]);

            Assert.True(rows.Data.Length >= 101);
            Assert.Equal(7, rows.GetRowId(0));
        }

        [Fact]
        public void OrdinaryBufferStartsSmall()
        {
            var rows = new TaggedRowBuffer(1);
            Assert.Empty(rows.Data);

            rows.AddRow(0, Row(1));
            Assert.Equal(PooledArray.MinGrowLength, rows.Data.Length);
        }

        [Fact]
        public void ArrayOverloadCopiesAWholeRow()
        {
            var rows = new TaggedRowBuffer(2);
            var source = Row(5, 6, 7);

            rows.AddRow(1, source, 1);

            Assert.Equal(Row(6, 7), rows.GetRow(0).ToArray());
            Assert.Equal(1, rows.GetRowId(0));
        }

        [Fact]
        public void AddingOneOfItsOwnRowsSurvivesGrowth()
        {
            var rows = new TaggedRowBuffer(2);
            rows.AddRow(0, Row(8, 9));

            for (var i = 0; i < 1000; i++)
                rows.AddRow(i + 1, rows.GetRow(i));

            for (var i = 0; i <= 1000; i++)
            {
                Assert.Equal(Row(8, 9), rows.GetRow(i, out var id).ToArray());
                Assert.Equal(i, id);
            }
        }

        [Fact]
        public void ClearAndDispose()
        {
            var rows = new TaggedRowBuffer(1);
            for (var i = 0; i < 400; i++)
                rows.AddRow(i, Row((uint)i));

            var data = rows.Data;
            rows.Clear();
            Assert.True(rows.IsEmpty);
            Assert.Same(data, rows.Data);

            rows.AddRow(1, Row(1));
            rows.Dispose();
            Assert.True(rows.IsEmpty);
            Assert.Empty(rows.Data);
            rows.Dispose();
            Assert.Equal(0, rows.AddRow(2, Row(2)));
        }

        [Fact]
        public void StructSinkCollectsAScan()
        {
            var source = new RowBuffer(2);
            for (uint i = 0; i < 10; i++)
                source.AddRow(Row(i, i * i));
            source.SetStale(3);

            var output = new TaggedRowBuffer(2);
            var sink = new TaggedRowSink(output);
            Scan(source, ref sink);

            Assert.Same(output, sink.Buffer);
            Assert.Equal(9, output.Count);
            var rows = All(output);
            Assert.DoesNotContain(rows, x => x.RowId == 3);
            Assert.Equal(Row(4, 16), rows.Single(x => x.RowId == 4).Row);
        }

        [Fact]
        public void BufferIsAnInterfaceSinkToo()
        {
            IRowSink sink = new TaggedRowBuffer(1);
            sink.AddRow(5, Row(50));

            var buffer = (TaggedRowBuffer)sink;
            Assert.Equal(Row(50), buffer.GetRow(0, out var id).ToArray());
            Assert.Equal(5, id);
            Assert.Throws<ArgumentNullException>(() => new TaggedRowSink(null!));
        }

        /// <summary>
        /// Random adds and stale marks against a list of tagged rows.
        /// </summary>
        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(3, 4)]
        [InlineData(4, 10)]
        public void MatchesAReferenceModel(int seed, int arity)
        {
            var rng = new Random(seed);
            var rows = new TaggedRowBuffer(arity);
            var model = new List<(int RowId, uint[] Physical)>();

            for (var step = 0; step < 3000; step++)
            {
                if (rng.Next(4) != 0 || model.Count == 0)
                {
                    var row = new uint[arity];
                    for (var c = 0; c < arity; c++)
                        row[c] = (uint)rng.Next(1000);

                    var rowId = rng.Next(1 << 20);
                    Assert.Equal(model.Count, rows.AddRow(rowId, row));
                    model.Add((rowId, row.Append((uint)rowId).ToArray()));
                }
                else
                {
                    var r = rng.Next(model.Count);
                    Assert.Equal(model[r].Physical[0] == Stale, rows.SetStale(r));
                    model[r].Physical[0] = Stale;
                }
            }

            Assert.Equal(model.Count, rows.Count);
            for (var r = 0; r < model.Count; r++)
                Assert.Equal(model[r].Physical, rows.Data.AsSpan(r * rows.Stride, rows.Stride).ToArray());

            var live = NonStale(rows);
            var expected = model.Where(x => x.Physical[0] != Stale).ToList();
            Assert.Equal(expected.Count, live.Count);
            for (var i = 0; i < live.Count; i++)
            {
                Assert.Equal((int)expected[i].Physical[arity], live[i].RowId);
                Assert.Equal(expected[i].Physical.Take(arity).ToArray(), live[i].Row);
            }
        }

        /// <summary>
        /// Growth drops the old storage to the garbage collector instead of returning it to the shared pool, so an
        /// in-place rewrite through a non-stale enumerator started before the growth (egglog's <c>non_stale_mut</c>
        /// pattern) lands in the old storage, never in an array someone else rented.
        /// </summary>
        [Fact]
        public void TaggedNonStaleEnumeratorWritesIntoAnArraySomeoneElseOwnsAfterGrowth()
        {
            var rows = new TaggedRowBuffer(1);
            for (var i = 0; i < 256; i++)
                rows.AddRow(i, new[] { (uint)i });
            Assert.Equal(512, rows.Data.Length);

            var e = rows.EnumerateNonStale();
            Assert.True(e.MoveNext());

            // Grows and leaves the 512-cell array to the garbage collector, not to the shared pool.
            rows.AddRow(256, new[] { 256u });
            var foreign = ArrayPool<uint>.Shared.Rent(512);
            Array.Clear(foreign);

            // An in-place rewrite through the enumerator (egglog's non_stale_mut pattern) lands in the old storage, so
            // the array rented from the pool stays zero.
            e.Current.Values[0] = 42;
            var corrupted = foreign[0];
            ArrayPool<uint>.Shared.Return(foreign);

            Assert.Equal(0u, corrupted);
        }

    }

}
