using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Helpers shared by the query tests.
    /// </summary>
    static class QueryTestUtil
    {

        /// <summary>
        /// Builds a column id.
        /// </summary>
        public static ColumnId C(int column) => new(column);

        /// <summary>
        /// Builds a constant value (egglog's <c>v</c>).
        /// </summary>
        public static Value V(uint value) => new(value);

        /// <summary>
        /// Creates an unsorted sorted-writes table with no rebuild columns whose merge rejects a different row with the
        /// same key, as egglog's tests' <c>panic!("merge not supported")</c> merges do.
        /// </summary>
        public static SortedWritesTable Strict(int keyCount, int arity)
        {
            var merge = new ClosureMerge((current, incoming) => current.AsSpan().SequenceEqual(incoming) ? null : throw new InvalidOperationException("merge not supported"));
            return new SortedWritesTable(keyCount, arity, null, ReadOnlySpan<ColumnId>.Empty, merge, ParallelHeuristics.Serial);
        }

        /// <summary>
        /// Creates an external function from a lambda that returns <c>null</c> to produce nothing.
        /// </summary>
        public static ExternalFunction Function(Func<uint[], uint?> body)
        {
            return ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                var r = body(args.ToArray());
                result = r ?? 0;
                return r.HasValue;
            });
        }

        /// <summary>
        /// Lists the live rows of a table, sorted lexicographically, as text.
        /// </summary>
        public static string[] Rows(Database db, TableId table) => DatabaseTestUtil.Rows(db, table).Select(TableTestUtil.Show).ToArray();

        /// <summary>
        /// Formats rows as <see cref="Rows"/> does, sorted.
        /// </summary>
        public static string[] Show(IEnumerable<uint[]> rows)
        {
            var list = rows.ToList();
            list.Sort(DatabaseTestUtil.CompareRows);
            return list.Select(TableTestUtil.Show).ToArray();
        }

        /// <summary>
        /// Lists the instructions of an action as text.
        /// </summary>
        public static string[] Instrs(ActionInfo action) => action.Instrs.Select(i => i.ToString()!).ToArray();

    }

}
