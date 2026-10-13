using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A table of <see cref="PlanTestDatabase"/>: rows as arrays, a key prefix that determines the rest, an optional
    /// sorted column (range constraints on it are fast, as on egglog's sort column) and uncacheable columns (equality
    /// constraints on them stay slow).
    /// </summary>
    sealed class PlanTestTable
    {

        public PlanTestTable(int arity, int keyCount, int sortColumn, bool[] uncacheable)
        {
            Arity = arity;
            KeyCount = keyCount;
            SortColumn = sortColumn;
            Uncacheable = uncacheable;
        }

        public int Arity { get; }

        public int KeyCount { get; }

        /// <summary>
        /// The column range constraints are fast on, or -1.
        /// </summary>
        public int SortColumn { get; }

        public bool[] Uncacheable { get; }

        public List<uint[]> Rows { get; } = new();

        /// <summary>
        /// Adds a row unless a row with the same key exists (the key determines the row, as in a function table).
        /// </summary>
        public void Add(uint[] row)
        {
            if (row.Length != Arity)
                throw new ArgumentException("Wrong arity.");

            foreach (var existing in Rows)
            {
                var same = true;
                for (var i = 0; i < KeyCount; i++)
                {
                    if (existing[i] != row[i])
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                    return;
            }

            Rows.Add(row);
        }

        public bool Matches(int row, ReadOnlySpan<Constraint> constraints) => Constraint.EvaluateAll(constraints, Rows[row]);

    }

}
