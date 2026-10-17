using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="RowBuffer"/>, including ports of egglog's core-relations/src/row_buffer/tests.rs and a
    /// randomized comparison against a list-of-rows reference.
    /// </summary>
    public class RowBufferTests
    {

        const uint Stale = Value.StaleRep;

        static uint[] Row(params uint[] cells) => cells;

        static void AssertCall(uint[] row, int oldRow, int newRow, (uint[] Row, int Old, int New) call)
        {
            Assert.Equal(row, call.Row);
            Assert.Equal(oldRow, call.Old);
            Assert.Equal(newRow, call.New);
        }

        /// <summary>
        /// Port of <c>remap_basic</c>.
        /// </summary>
        [Fact]
        public void RemapBasic()
        {
            var rows = new RowBuffer(2);
            var r1 = rows.AddRow(Row(0, 1));
            var r2 = rows.AddRow(Row(2, 3));
            var r3 = rows.AddRow(Row(4, 5));
            var r4 = rows.AddRow(Row(6, 7));

            Assert.False(rows.SetStale(r2));
            Assert.False(rows.SetStale(r3));

            var got = new RowRemapRecorder();
            rows.RemoveStale(ref got);

            Assert.Equal(2, got.Calls.Count);
            AssertCall(Row(0, 1), r1, r1, got.Calls[0]);
            AssertCall(Row(6, 7), r4, r2, got.Calls[1]);
            Assert.Equal(Row(6, 7), rows.GetRow(r2).ToArray());
            Assert.Equal(2, rows.Count);
        }

        /// <summary>
        /// Port of <c>remap_narrow</c>.
        /// </summary>
        [Fact]
        public void RemapNarrow()
        {
            var rows = new RowBuffer(1);
            var r1 = rows.AddRow(Row(0));
            var r2 = rows.AddRow(Row(1));
            var r3 = rows.AddRow(Row(2));
            var r4 = rows.AddRow(Row(3));

            rows.SetStale(r2);
            rows.SetStale(r3);

            var got = new RowRemapRecorder();
            rows.RemoveStale(ref got);

            Assert.Equal(2, got.Calls.Count);
            AssertCall(Row(0), r1, r1, got.Calls[0]);
            AssertCall(Row(3), r4, r2, got.Calls[1]);
            Assert.Equal(Row(3), rows.GetRow(r2).ToArray());
        }

        [Fact]
        public void ArityZeroIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RowBuffer(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RowBuffer(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RowBuffer(0, 10));
        }

        [Fact]
        public void RowsGetDenseIds()
        {
            var rows = new RowBuffer(3);

            for (var i = 0; i < 1000; i++)
                Assert.Equal(i, rows.AddRow(Row((uint)i, (uint)i + 1, (uint)i + 2)));

            Assert.Equal(1000, rows.Count);
            Assert.Equal(3000, rows.CellCount);
            for (var i = 0; i < 1000; i++)
                Assert.Equal(Row((uint)i, (uint)i + 1, (uint)i + 2), rows.GetRow(i).ToArray());
        }

        [Fact]
        public void WrongArityIsRejected()
        {
            var rows = new RowBuffer(2);

            Assert.Throws<ArgumentException>(() => rows.AddRow(Row(1)));
            Assert.Throws<ArgumentException>(() => rows.AddRow(Row(1, 2, 3)));
            Assert.Equal(0, rows.Count);
        }

        [Fact]
        public void OutOfRangeRowsAreRejected()
        {
            var rows = new RowBuffer(2, 8);
            rows.AddRow(Row(1, 2));

            Assert.Throws<ArgumentOutOfRangeException>(() => rows.GetRow(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.GetRow(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.GetMutableRow(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.SetStale(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.IsStale(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.RowOffset(1));
        }

        [Fact]
        public void SetStaleIsIdempotentAndReportsThePreviousState()
        {
            var rows = new RowBuffer(2);
            rows.AddRow(Row(1, 2));

            Assert.False(rows.IsStale(0));
            Assert.False(rows.SetStale(0));
            Assert.True(rows.IsStale(0));
            Assert.True(rows.SetStale(0));
            Assert.True(rows.SetStaleShared(0));
            Assert.Equal(Row(Stale, 2), rows.GetRow(0).ToArray());
        }

        [Fact]
        public void EnumerationIncludesOrSkipsStaleRows()
        {
            var rows = new RowBuffer(2);
            for (uint i = 0; i < 6; i++)
                rows.AddRow(Row(i, i * 10));

            rows.SetStale(0);
            rows.SetStale(3);
            rows.SetStale(5);

            var all = new List<uint[]>();
            foreach (var row in rows.EnumerateRows())
                all.Add(row.ToArray());

            Assert.Equal(6, all.Count);
            Assert.Equal(Row(Stale, 0), all[0]);

            var live = new List<(int Id, uint[] Row)>();
            var e = rows.EnumerateNonStale();
            while (e.MoveNext())
                live.Add((e.Row, e.Current.ToArray()));

            Assert.Equal(new[] { 1, 2, 4 }, live.Select(x => x.Id).ToArray());
            Assert.Equal(Row(4, 40), live[2].Row);
            Assert.False(e.MoveNext());
        }

        [Fact]
        public void EnumeratingAnEmptyOrAllStaleBufferYieldsNothing()
        {
            var rows = new RowBuffer(1);
            foreach (var row in rows.EnumerateNonStale())
                Assert.Fail("empty");

            rows.AddRow(Row(1));
            rows.AddRow(Row(2));
            rows.SetStale(0);
            rows.SetStale(1);
            foreach (var row in rows.EnumerateNonStale())
                Assert.Fail("all stale");
        }

        [Fact]
        public void CurrentIsEmptyOutsideTheRows()
        {
            var rows = new RowBuffer(2);
            rows.AddRow(Row(1, 2));

            var all = rows.EnumerateRows();
            Assert.True(all.Current.IsEmpty);
            Assert.True(all.MoveNext());
            Assert.Equal(Row(1, 2), all.Current.ToArray());
            Assert.False(all.MoveNext());
            Assert.True(all.Current.IsEmpty);
            Assert.False(all.MoveNext());
            Assert.Equal(1, all.Row);

            var live = rows.EnumerateNonStale();
            Assert.True(live.Current.IsEmpty);
            Assert.True(live.MoveNext());
            Assert.Equal(0, live.Row);
            Assert.False(live.MoveNext());
            Assert.True(live.Current.IsEmpty);
            Assert.False(live.MoveNext());
        }

        /// <summary>
        /// Growth drops the old storage to the garbage collector instead of returning it to the shared pool, so an
        /// enumerator started before the growth keeps reading its rows, and nobody can rent that array.
        /// </summary>
        [Fact]
        public void GrowthDoesNotRecycleTheStorageAnEnumeratorReads()
        {
            var rows = new RowBuffer(2);
            for (uint i = 0; i < 256; i++)
                rows.AddRow(Row(i, i));
            var old = rows.Data;
            Assert.Equal(512, old.Length);

            var e = rows.EnumerateNonStale();
            Assert.True(e.MoveNext());
            rows.AddRow(Row(256, 256));
            Assert.NotSame(old, rows.Data);

            var rented = ArrayPool<uint>.Shared.Rent(512);
            Assert.NotSame(old, rented);
            ArrayPool<uint>.Shared.Return(rented);

            var seen = 1;
            while (e.MoveNext())
            {
                Assert.Equal(Row((uint)e.Row, (uint)e.Row), e.Current.ToArray());
                seen++;
            }

            Assert.Equal(256, seen);
        }

        [Fact]
        public void ReserveDoesNotRecycleTheOldStorage()
        {
            var rows = new RowBuffer(1);
            for (uint i = 0; i < 512; i++)
                rows.AddRow(Row(i));
            var old = rows.Data;

            rows.Reserve(10_000);

            var rented = ArrayPool<uint>.Shared.Rent(512);
            Assert.NotSame(old, rented);
            ArrayPool<uint>.Shared.Return(rented);
            Assert.Equal(511u, rows.GetRow(511)[0]);
        }

        [Fact]
        public void MutableRowsWriteThrough()
        {
            var rows = new RowBuffer(2);
            rows.AddRow(Row(1, 2));
            rows.AddRow(Row(3, 4));

            rows.GetMutableRow(1)[1] = 40;
            foreach (var row in rows.EnumerateNonStale())
                row[0] += 100;

            Assert.Equal(Row(101, 2), rows.GetRow(0).ToArray());
            Assert.Equal(Row(103, 40), rows.GetRow(1).ToArray());
        }

        [Fact]
        public void AddingOneOfItsOwnRowsSurvivesGrowth()
        {
            var rows = new RowBuffer(3);
            rows.AddRow(Row(1, 2, 3));

            for (var i = 0; i < 2000; i++)
            {
                var id = rows.AddRow(rows.GetRow(i));
                Assert.Equal(Row(1, 2, 3), rows.GetRow(id).ToArray());
            }
        }

        [Fact]
        public void RawAccessMatchesRows()
        {
            var rows = new RowBuffer(3);
            for (uint i = 0; i < 50; i++)
                rows.AddRow(Row(i, i + 100, i + 200));

            var data = rows.Data;
            for (var r = 0; r < rows.Count; r++)
            {
                var offset = rows.RowOffset(r);
                Assert.Equal(r * rows.Arity, offset);
                Assert.Equal((uint)r + 100, data[offset + 1]);
                Assert.Equal(rows.GetRow(r).ToArray(), rows.GetRowUnchecked(r).ToArray());
            }
        }

        [Fact]
        public void ReserveAvoidsGrowth()
        {
            var rows = new RowBuffer(4);
            rows.AddRow(Row(1, 2, 3, 4));
            rows.Reserve(1000);
            var data = rows.Data;
            Assert.True(rows.Capacity >= 1001);

            for (uint i = 0; i < 1000; i++)
                rows.AddRow(Row(i, i, i, i));

            Assert.Same(data, rows.Data);
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.Reserve(-1));
        }

        [Fact]
        public void SetCountPublishesRowsWrittenDirectly()
        {
            var rows = new RowBuffer(2);
            rows.Reserve(100);

            Parallel.For(0, 100, r =>
            {
                rows.Data[r * 2] = (uint)r;
                rows.Data[r * 2 + 1] = (uint)r * 2;
            });
            rows.SetCount(100);

            Assert.Equal(100, rows.Count);
            Assert.Equal(Row(42, 84), rows.GetRow(42).ToArray());
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.SetCount(rows.Capacity + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.SetCount(-1));
        }

        [Fact]
        public void ClearKeepsTheStorage()
        {
            var rows = new RowBuffer(2);
            for (uint i = 0; i < 500; i++)
                rows.AddRow(Row(i, i));

            var data = rows.Data;
            rows.Clear();

            Assert.Equal(0, rows.Count);
            Assert.True(rows.IsEmpty);
            Assert.Same(data, rows.Data);
            Assert.Equal(0, rows.AddRow(Row(9, 9)));
        }

        [Fact]
        public void CloneIsIndependent()
        {
            var rows = new RowBuffer(2);
            for (uint i = 0; i < 300; i++)
                rows.AddRow(Row(i, i + 1));
            rows.SetStale(7);

            var copy = rows.Clone();
            rows.GetMutableRow(0)[1] = 99;
            rows.AddRow(Row(5, 5));

            Assert.Equal(300, copy.Count);
            Assert.Equal(2, copy.Arity);
            Assert.Equal(Row(0, 1), copy.GetRow(0).ToArray());
            Assert.True(copy.IsStale(7));
            Assert.NotSame(rows.Data, copy.Data);
        }

        [Fact]
        public void DisposeEmptiesTheBufferAndIsIdempotent()
        {
            var rows = new RowBuffer(2);
            for (uint i = 0; i < 500; i++)
                rows.AddRow(Row(i, i));

            rows.Dispose();
            Assert.Equal(0, rows.Count);
            Assert.Empty(rows.Data);
            rows.Dispose();

            Assert.Equal(0, rows.AddRow(Row(1, 2)));
            Assert.Equal(Row(1, 2), rows.GetRow(0).ToArray());
        }

        [Fact]
        public void RemoveStaleWithoutCallbackCompacts()
        {
            var rows = new RowBuffer(2);
            for (uint i = 0; i < 10; i++)
                rows.AddRow(Row(i, i));
            for (var i = 0; i < 10; i += 2)
                rows.SetStale(i);

            rows.RemoveStale();

            Assert.Equal(5, rows.Count);
            for (var i = 0; i < 5; i++)
                Assert.Equal(Row((uint)(2 * i + 1), (uint)(2 * i + 1)), rows.GetRow(i).ToArray());
        }

        [Fact]
        public void RemoveStaleOnEmptyAndAllStaleBuffers()
        {
            var rows = new RowBuffer(3);
            rows.RemoveStale();
            Assert.Equal(0, rows.Count);

            rows.AddRow(Row(1, 2, 3));
            rows.AddRow(Row(4, 5, 6));
            rows.SetStale(0);
            rows.SetStale(1);
            var got = new RowRemapRecorder();
            rows.RemoveStale(ref got);

            Assert.Empty(got.Calls);
            Assert.Equal(0, rows.Count);
        }

        [Fact]
        public void SetStaleSharedFromManyThreadsOnDistinctRows()
        {
            var rows = new RowBuffer(2);
            for (uint i = 0; i < 50000; i++)
                rows.AddRow(Row(i, i));

            var newlyStale = 0;
            Parallel.For(0, 8, worker =>
            {
                var mine = 0;
                for (var r = worker; r < rows.Count; r += 8)
                    if (r % 3 == 0 && rows.SetStaleShared(r) == false)
                        mine++;

                Interlocked.Add(ref newlyStale, mine);
            });

            Assert.Equal(Enumerable.Range(0, 50000).Count(r => r % 3 == 0), newlyStale);
            for (var r = 0; r < rows.Count; r++)
                Assert.Equal(r % 3 == 0, rows.IsStale(r));
        }

        /// <summary>
        /// Random adds, stale marks, compactions and clears, checked after every step against a list of rows. Rows are
        /// sometimes added already stale, as tables do.
        /// </summary>
        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(4, 5)]
        [InlineData(5, 9)]
        [InlineData(6, 12)]
        public void MatchesAReferenceModel(int seed, int arity)
        {
            var rng = new Random(seed);
            var rows = new RowBuffer(arity);
            var model = new List<uint[]>();

            for (var step = 0; step < 4000; step++)
            {
                var op = rng.Next(100);
                if (op < 55)
                {
                    var row = new uint[arity];
                    for (var c = 0; c < arity; c++)
                        row[c] = (uint)rng.Next(1000);
                    if (rng.Next(20) == 0)
                        row[0] = Stale;

                    Assert.Equal(model.Count, rows.AddRow(row));
                    model.Add(row);
                }
                else if (op < 85)
                {
                    if (model.Count == 0)
                        continue;

                    var r = rng.Next(model.Count);
                    Assert.Equal(model[r][0] == Stale, rows.SetStale(r));
                    model[r][0] = Stale;
                }
                else if (op < 98)
                {
                    var expected = new List<(uint[] Row, int Old, int New)>();
                    var kept = new List<uint[]>();
                    for (var r = 0; r < model.Count; r++)
                    {
                        if (model[r][0] == Stale)
                            continue;

                        expected.Add((model[r], r, kept.Count));
                        kept.Add(model[r]);
                    }

                    var got = new RowRemapRecorder();
                    rows.RemoveStale(ref got);
                    Assert.Equal(expected.Count, got.Calls.Count);
                    for (var i = 0; i < expected.Count; i++)
                        AssertCall(expected[i].Row, expected[i].Old, expected[i].New, got.Calls[i]);

                    model = kept;
                }
                else
                {
                    rows.Clear();
                    model.Clear();
                }

                Assert.Equal(model.Count, rows.Count);
                if (step % 50 == 0 || op >= 85)
                {
                    for (var r = 0; r < model.Count; r++)
                        Assert.Equal(model[r], rows.GetRow(r).ToArray());

                    var live = new List<uint[]>();
                    foreach (var row in rows.EnumerateNonStale())
                        live.Add(row.ToArray());
                    Assert.Equal(model.Where(x => x[0] != Stale).ToList(), live);
                }
            }
        }

        /// <summary>
        /// Growth drops the old storage to the garbage collector, so an enumerator started before the growth keeps
        /// reading its old storage, as documented, while other code rents an array of the same length from the shared
        /// pool.
        /// </summary>
        [Fact]
        public void RowEnumeratorKeepsReadingItsOldStorageAfterGrowth()
        {
            var rows = new RowBuffer(1);
            for (uint i = 0; i < 512; i++)
                rows.AddRow(new[] { i });
            Assert.Equal(512, rows.Data.Length);

            var e = rows.EnumerateRows();
            Assert.True(e.MoveNext());

            // Grows to 1024 cells and leaves the full 512-cell array to the garbage collector, not to the shared pool.
            rows.AddRow(new[] { 999u });

            // Other code on this thread rents a 512-cell array from the shared pool and fills it.
            var foreign = ArrayPool<uint>.Shared.Rent(512);
            Array.Fill(foreign, 0xDEADu);

            Assert.True(e.MoveNext());
            var seen = e.Current[0];
            ArrayPool<uint>.Shared.Return(foreign);

            // The documented behaviour: the enumerator still reads the old storage, whose row 1 holds 1.
            Assert.Equal(1u, seen);
        }

    }

}
