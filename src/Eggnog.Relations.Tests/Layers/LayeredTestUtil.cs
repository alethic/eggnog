using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Helpers shared by the layered-table tests (colored-contexts spec §6): lattice tables of the shapes the tests use,
    /// and one-row writes through <see cref="Context.Write{TResult}(Func{ExecutionState, TResult})"/>.
    /// </summary>
    static class LayeredTestUtil
    {

        /// <summary>
        /// Builds a column id.
        /// </summary>
        public static ColumnId C(int column) => new(column);

        /// <summary>
        /// A function <c>f(k0..kn) → v</c> with a timestamp column, merged by the larger value (a lattice merge): keys are
        /// ids (rebuild columns), the value is a number. Columns: keys, value, timestamp.
        /// </summary>
        public static SortedWritesTable MaxFunction(int keys) =>
            new(keys, keys + 2, C(keys + 1), Enumerable.Range(0, keys).Select(C).ToArray(), new MaxMerge(C(keys)), ParallelHeuristics.Serial);

        /// <summary>
        /// A relation <c>r(k0..kn)</c> with a timestamp column, merged by the smaller timestamp, so re-asserting a fact
        /// changes nothing (a lattice merge). Columns: keys, timestamp.
        /// </summary>
        public static SortedWritesTable Relation(int keys) =>
            new(keys, keys + 1, C(keys), Enumerable.Range(0, keys).Select(C).ToArray(), new MinMerge(C(keys)), ParallelHeuristics.Serial);

        /// <summary>
        /// A function with a value but no id column and no timestamp: <c>f(k) → v</c> by the larger value, nothing to
        /// rebuild (a value-only table).
        /// </summary>
        public static SortedWritesTable ValueOnly() => new(1, 2, null, [], new MaxMerge(C(1)), ParallelHeuristics.Serial);

        /// <summary>
        /// A constructor <c>f(k0..kn) → id</c> with a timestamp column, merged by <see cref="UnionIdMerge"/>:
        /// <see cref="StoreTests.Function"/>.
        /// </summary>
        public static SortedWritesTable Constructor(Store store, int keys = 1) => StoreTests.Function(store, keys);

        /// <summary>
        /// Writes <paramref name="row"/> into <paramref name="table"/> in one batch of <paramref name="context"/>, with the
        /// batch's epoch in the timestamp column when the table has one (the given cell is overwritten).
        /// </summary>
        public static void Insert(Context context, TableId table, params uint[] row)
        {
            var sort = context.Store.Schema[table]!.SortColumn;
            context.Write(state =>
            {
                var copy = (uint[])row.Clone();
                if (sort >= 0)
                    copy[sort] = state.Timestamp;

                state.StageInsert(table, copy);
                return 0;
            });
        }

        /// <summary>
        /// Deletes the row with key <paramref name="key"/> from <paramref name="table"/> in one batch of
        /// <paramref name="context"/>.
        /// </summary>
        public static void Remove(Context context, TableId table, params uint[] key)
        {
            context.Write(state =>
            {
                state.StageRemove(table, key);
                return 0;
            });
        }

        /// <summary>
        /// Gets the row <paramref name="context"/> sees for <paramref name="key"/>, or <c>null</c>.
        /// </summary>
        public static uint[]? Row(Context context, TableId table, params uint[] key)
        {
            var row = new uint[context.Store.Schema.GetSpec(table).Arity];
            return context.TryGetRow(table, key, row) ? row : null;
        }

        /// <summary>
        /// Lists the rows of <paramref name="view"/> its context sees, by scanning each present layer and dropping hidden
        /// rows (<see cref="HiddenSkippingSink{TSink}"/>): the "filtered scan" path of <c>LookupPathsAgree</c>.
        /// </summary>
        public static List<(int Layer, int Row, uint[] Cells)> ScanVisible(TableView view)
        {
            var result = new List<(int, int, uint[])>();
            for (var j = 0; j < view.LayerCount; j++)
            {
                if (view.Layers[j] is not { } layer)
                    continue;

                var sink = new HiddenSkippingSink<RowCollector>(new RowCollector(), layer.Hidden);
                var all = layer.Table.All();
                layer.Table.ScanGeneric(all.AsRef(), ref sink);
                all.Dispose();
                foreach (var (id, row) in sink.Inner.Rows)
                    result.Add((j, id, row));
            }

            return result;
        }

        /// <summary>
        /// Formats a key for dictionaries.
        /// </summary>
        public static string KeyOf(ReadOnlySpan<uint> key) => string.Join(",", key.ToArray());

    }

}
