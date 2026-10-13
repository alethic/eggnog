using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's <c>Prober</c> (core-relations/src/free_join/execute.rs:211-345): clamping a whole-table index to the
    /// atom's range, and the key count of a clamped prober, which counts at most the range's rows (egglog's
    /// <c>Prober::len</c> ignores the clamp and counts every key of the index).
    /// </summary>
    public class ProberTests
    {

        static (SortedWritesTable Table, ColumnIndex Index) Indexed()
        {
            // Value i % 10 for rows 0..99: every value occurs in every tenth row.
            var table = TableTestUtil.FillTable(Enumerable.Range(0, 100).Select(i => new[] { (uint)i, (uint)(i % 10) }), 1, null, ClosureMerge.TakeNew);
            var index = table.GroupByColumn(table.All().AsRef(), new ColumnId(1));
            return (table, index);
        }

        [Fact]
        public void ClampedProbesSeeOnlyTheRange()
        {
            var (_, index) = Indexed();
            var prober = new ProberPiece { Kind = ProberKind.CachedColumn, Column = index, HasOuter = true, Outer = new OffsetRange(40, 47) };
            Assert.True(prober.TryGetSubset(3, out var rows));
            Assert.Equal([43], ExecutionTestUtil.Ids(rows));
            Assert.False(prober.TryGetSubset(8, out _));

            var keys = new List<uint>();
            var cursor = default(ProberCursor);
            while (cursor.MoveNext(prober))
            {
                keys.Add(cursor.Key);
                Assert.Equal([40 + (int)cursor.Key], ExecutionTestUtil.Ids(cursor.Rows));
            }

            Assert.Equal([0u, 1u, 2u, 3u, 4u, 5u, 6u], keys.OrderBy(k => k));
        }

        [Fact]
        public void ClampedCountIsAtMostTheRangeSize()
        {
            var (_, index) = Indexed();
            var whole = new ProberPiece { Kind = ProberKind.CachedColumn, Column = index };
            Assert.Equal(10, whole.Count);

            // egglog reports 10 here, the whole index's key count.
            var clamped = new ProberPiece { Kind = ProberKind.CachedColumn, Column = index, HasOuter = true, Outer = new OffsetRange(51, 55) };
            Assert.Equal(4, clamped.Count);
        }

        [Fact]
        public void TupleProbesClampToo()
        {
            var (table, _) = Indexed();
            var tuple = table.GroupByKey(table.All().AsRef(), [new ColumnId(1), new ColumnId(0)]);
            var prober = new ProberPiece { Kind = ProberKind.CachedTuple, Tuple = tuple, HasOuter = true, Outer = new OffsetRange(0, 50) };
            Assert.True(prober.TryGetSubset([3u, 13u], out var rows));
            Assert.Equal([13], ExecutionTestUtil.Ids(rows));
            Assert.False(prober.TryGetSubset([3u, 63u], out _));
            Assert.Equal(50, prober.Count);
        }

    }

}
