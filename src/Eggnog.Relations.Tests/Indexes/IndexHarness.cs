using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A <see cref="SortedWritesTable"/> with columns <c>[k0, k1, a, b, ts]</c> (key <c>[k0, k1]</c>, sorted by
    /// <c>ts</c>, the incoming row wins conflicts) and three indexes over it: one-column on <c>a</c>, multi-column on
    /// <c>(k0, a, b)</c>, and tuple on <c>(b, k1)</c>. Used by <see cref="IndexPropertyTests"/>.
    /// </summary>
    sealed class IndexHarness
    {

        /// <summary>
        /// The first key column.
        /// </summary>
        public const int K0 = 0;

        /// <summary>
        /// The second key column.
        /// </summary>
        public const int K1 = 1;

        /// <summary>
        /// The first value column.
        /// </summary>
        public const int A = 2;

        /// <summary>
        /// The second value column.
        /// </summary>
        public const int B = 3;

        /// <summary>
        /// The timestamp column.
        /// </summary>
        public const int Ts = 4;

        /// <summary>
        /// The column of the one-column index.
        /// </summary>
        public static readonly int[] SingleColumns = [A];

        /// <summary>
        /// The columns of the multi-column index; <c>k0</c> and the values share a domain, so rows repeat values.
        /// </summary>
        public static readonly int[] MultiColumns = [K0, A, B];

        /// <summary>
        /// The key columns of the tuple index, out of table order.
        /// </summary>
        public static readonly int[] TupleColumns = [B, K1];

        /// <summary>
        /// Initializes the table and empty indexes.
        /// </summary>
        /// <param name="parallelism">The table's and the indexes' parallelism.</param>
        public IndexHarness(ParallelHeuristics parallelism)
        {
            Parallelism = parallelism;
            Table = new SortedWritesTable(2, 5, new ColumnId(Ts), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, parallelism);
            Single = new Index<ColumnIndex>(IndexTestUtil.Ids(SingleColumns), new ColumnIndex(parallelism));
            Multi = new Index<ColumnIndex>(IndexTestUtil.Ids(MultiColumns), new ColumnIndex(parallelism));
            Tuple = new Index<TupleIndex>(IndexTestUtil.Ids(TupleColumns), new TupleIndex(TupleColumns.Length, parallelism));
        }

        /// <summary>
        /// Gets the parallelism settings.
        /// </summary>
        public ParallelHeuristics Parallelism { get; }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public SortedWritesTable Table { get; }

        /// <summary>
        /// Gets the one-column index.
        /// </summary>
        public Index<ColumnIndex> Single { get; }

        /// <summary>
        /// Gets the multi-column index.
        /// </summary>
        public Index<ColumnIndex> Multi { get; }

        /// <summary>
        /// Gets the tuple index.
        /// </summary>
        public Index<TupleIndex> Tuple { get; }

        /// <summary>
        /// Refreshes the three indexes and checks their versions.
        /// </summary>
        public void Refresh()
        {
            Single.Refresh(Table);
            Multi.Refresh(Table);
            Tuple.Refresh(Table);
            Assert.Equal(Table.Version, Single.UpdatedTo);
            Assert.False(Multi.NeedsRefresh(Table));
            Assert.False(Tuple.NeedsRefresh(Table));
        }

        /// <summary>
        /// Checks the three indexes against the naive group-by of the live rows.
        /// </summary>
        /// <param name="what">The step, for messages.</param>
        public void CheckAgainstModel(string what)
        {
            IndexTestUtil.AssertCoversLiveRows(Table, IndexTestUtil.GroupByValue(Table, SingleColumns), IndexTestUtil.Entries(Single.Data), what + " single");
            IndexTestUtil.AssertCoversLiveRows(Table, IndexTestUtil.GroupByValue(Table, MultiColumns), IndexTestUtil.Entries(Multi.Data), what + " multi");
            IndexTestUtil.AssertCoversLiveRows(Table, IndexTestUtil.GroupByTuple(Table, TupleColumns), IndexTestUtil.Entries(Tuple.Data), what + " tuple");
        }

        /// <summary>
        /// Builds the indexes from scratch, through <see cref="Index{TIndex}.Refresh"/> from version (0, 0) and through
        /// each build path directly (row at a time, sorted, parallel), and checks that each lists exactly the live rows.
        /// </summary>
        /// <param name="what">The step, for messages.</param>
        public void CheckFreshBuilds(string what)
        {
            // Incremental against generation zero, a full (sort-based) rebuild otherwise, parallel when large.
            var single = new Index<ColumnIndex>(IndexTestUtil.Ids(SingleColumns), new ColumnIndex(Parallelism));
            var multi = new Index<ColumnIndex>(IndexTestUtil.Ids(MultiColumns), new ColumnIndex(Parallelism));
            var tuple = new Index<TupleIndex>(IndexTestUtil.Ids(TupleColumns), new TupleIndex(TupleColumns.Length, Parallelism));
            single.Refresh(Table);
            multi.Refresh(Table);
            tuple.Refresh(Table);
            IndexTestUtil.AssertExactly(IndexTestUtil.GroupByValue(Table, SingleColumns), IndexTestUtil.Entries(single.Data), what + " fresh single");
            IndexTestUtil.AssertExactly(IndexTestUtil.GroupByValue(Table, MultiColumns), IndexTestUtil.Entries(multi.Data), what + " fresh multi");
            IndexTestUtil.AssertExactly(IndexTestUtil.GroupByTuple(Table, TupleColumns), IndexTestUtil.Entries(tuple.Data), what + " fresh tuple");

            var all = Table.All();
            foreach (var path in new[] { "serial", "sorted", "parallel" })
            {
                var column = new ColumnIndex(Parallelism);
                var columns = IndexTestUtil.Ids(MultiColumns);
                if (path == "serial")
                    column.MergeSubset(Table, columns, all.AsRef());
                else if (path == "sorted")
                    column.RebuildFull(Table, columns, all.AsRef());
                else
                    column.MergeParallel(Table, columns, all.AsRef());

                IndexTestUtil.AssertExactly(IndexTestUtil.GroupByValue(Table, MultiColumns), IndexTestUtil.Entries(column), $"{what} {path} multi");

                var keyed = new TupleIndex(TupleColumns.Length, Parallelism);
                if (path == "parallel")
                    keyed.MergeParallel(Table, IndexTestUtil.Ids(TupleColumns), all.AsRef());
                else
                    keyed.RebuildFull(Table, IndexTestUtil.Ids(TupleColumns), all.AsRef());

                IndexTestUtil.AssertExactly(IndexTestUtil.GroupByTuple(Table, TupleColumns), IndexTestUtil.Entries(keyed), $"{what} {path} tuple");
            }
        }

    }

}
