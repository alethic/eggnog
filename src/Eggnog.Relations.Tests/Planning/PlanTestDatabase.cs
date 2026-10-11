using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A minimal in-memory database for planner tests: it describes tables to <see cref="QueryBuilder"/>, answers the
    /// planner's statistics from the rows (subset sizes after fast constraints, distinct counts per column, as egglog's
    /// <c>approx_size</c> and <c>col_uniqueness</c> do) and evaluates headers into subsets.
    /// </summary>
    sealed class PlanTestDatabase : IQuerySchema, IPlanStatistics, IHeaderEvaluator
    {

        readonly List<PlanTestTable> tables = new();

        public PlanTestTable this[TableId table] => tables[table.Index];

        public TableId AddTable(int arity, int keyCount, IEnumerable<uint[]> rows, int sortColumn = -1, bool[]? uncacheable = null)
        {
            var t = new PlanTestTable(arity, keyCount, sortColumn, uncacheable ?? new bool[arity]);
            foreach (var row in rows)
                t.Add(row);

            tables.Add(t);
            return new TableId(tables.Count - 1);
        }

        /// <summary>
        /// The rows of the planner experiments (plan_dump.rs): <c>(i % m, (7i + 3) % (n + 1))</c> for <c>i</c> below
        /// <paramref name="n"/>.
        /// </summary>
        public static IEnumerable<uint[]> ExperimentRows(int n, int m)
        {
            for (var i = 0; i < n; i++)
                yield return [(uint)(i % m), (uint)((i * 7 + 3) % (n + 1))];
        }

        public int GetArity(TableId table) => tables[table.Index].Arity;

        public int GetKeyCount(TableId table) => tables[table.Index].KeyCount;

        /// <summary>
        /// egglog's split: the table's own fast constraints first (ranges and equalities on the sort column, as
        /// <c>SortedWritesTable::fast_subset</c>), then equalities on cacheable columns, promoted through a column index.
        /// </summary>
        public void SplitConstraints(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow)
        {
            var t = tables[table.Index];
            var rest = new List<Constraint>();
            foreach (var c in constraints)
            {
                if (c.Kind != ConstraintKind.Eq && c.Column == t.SortColumn)
                    fast.Add(c);
                else
                    rest.Add(c);
            }

            foreach (var c in rest)
            {
                if (c.Kind == ConstraintKind.EqConst && t.Uncacheable[c.Column] == false)
                    fast.Add(c);
                else
                    slow.Add(c);
            }
        }

        public long GetApproximateSize(AtomId atom, QueryAtom info)
        {
            var t = tables[info.Table.Index];
            var n = 0;
            for (var r = 0; r < t.Rows.Count; r++)
                if (t.Matches(r, info.FastConstraints))
                    n++;

            return n;
        }

        public ColumnUniqueness GetColumnUniqueness(TableId table, ColumnId column)
        {
            var t = tables[table.Index];
            var distinct = new HashSet<uint>();
            foreach (var row in t.Rows)
                distinct.Add(row[column.Index]);

            return new ColumnUniqueness((ulong)t.Rows.Count, (ulong)distinct.Count);
        }

        public Subset Evaluate(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow)
        {
            SplitConstraints(table, constraints, fast, slow);
            var t = tables[table.Index];
            var rows = new List<int>();
            for (var r = 0; r < t.Rows.Count; r++)
                if (t.Matches(r, CollectionsMarshal.AsSpan(fast)))
                    rows.Add(r);

            if (rows.Count == t.Rows.Count)
                return Subset.Dense(0, rows.Count);

            var buffer = OffsetBufferPool.Rent(rows.Count);
            rows.CopyTo(buffer);
            return Subset.FromOwnedRun(buffer, 0, rows.Count);
        }

    }

}
