using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A union-find and a function table <c>f(a, b) = x</c> with columns <c>[a, b, x, ts]</c>, sorted by timestamp, in
    /// one catalog: the setting of the rebuild tests. With <c>"union"</c>, <c>x</c> is an id merged by
    /// <see cref="UnionIdMerge"/> and all three of <c>a, b, x</c> are rebuilt; with <c>"sum"</c>, <c>x</c> is a count
    /// merged by <see cref="SumMerge"/>, which is not idempotent, and only <c>a, b</c> are rebuilt.
    /// </summary>
    sealed class RebuildUniverse
    {

        static ColumnId C(int column) => new(column);

        int freshProvider = 1000;

        /// <summary>
        /// Initializes the universe.
        /// </summary>
        /// <param name="merge"><c>"union"</c> or <c>"sum"</c>.</param>
        /// <param name="parallelism">The table's parallelism.</param>
        public RebuildUniverse(string merge, ParallelHeuristics parallelism)
        {
            Merge = merge;
            Catalog = new TableCatalog();
            Uf = new DisplacedTable();
            UfId = Catalog.Add(Uf);
            Table = merge == "union"
                ? new SortedWritesTable(2, 4, C(3), [C(0), C(1), C(2)], new UnionIdMerge(C(2), UfId, C(3)), parallelism)
                : new SortedWritesTable(2, 4, C(3), [C(0), C(1)], new SumMerge(2), parallelism);
            Catalog.Add(Table);
            State = new ExecutionState(Catalog, 1);
        }

        /// <summary>
        /// Gets the merge name.
        /// </summary>
        public string Merge { get; }

        /// <summary>
        /// Gets the catalog.
        /// </summary>
        public TableCatalog Catalog { get; }

        /// <summary>
        /// Gets the union-find.
        /// </summary>
        public DisplacedTable Uf { get; }

        /// <summary>
        /// Gets the union-find's id.
        /// </summary>
        public TableId UfId { get; }

        /// <summary>
        /// Gets the function table.
        /// </summary>
        public SortedWritesTable Table { get; }

        /// <summary>
        /// Gets the execution state merges and rebuilds run in.
        /// </summary>
        public ExecutionState State { get; }

        /// <summary>
        /// Gets the next timestamp.
        /// </summary>
        public uint Timestamp { get; private set; } = 1;

        /// <summary>
        /// Stages unions and merges them into the union-find.
        /// </summary>
        /// <param name="pairs">The pairs to union.</param>
        public void Union(IEnumerable<(uint A, uint B)> pairs)
        {
            TableTestUtil.Stage(Uf, pairs.Select(p => new[] { p.A, p.B, Timestamp }).ToArray());
            Uf.Merge(State);
            Timestamp++;
        }

        /// <summary>
        /// Stages rows (canonicalized, stamped with the current timestamp) and removals, then merges the table and the
        /// unions its merge function staged.
        /// </summary>
        /// <param name="rows">The rows, <c>[a, b, x]</c>.</param>
        /// <param name="removals">The keys to remove, <c>[a, b]</c>.</param>
        public void Write(IEnumerable<uint[]> rows, IEnumerable<uint[]> removals)
        {
            TableTestUtil.StageRemove(Table, removals.Select(k => new[] { Uf.FindNaive(k[0]), Uf.FindNaive(k[1]) }).ToArray());
            TableTestUtil.Stage(Table, rows.Select(r => new[] { Uf.FindNaive(r[0]), Uf.FindNaive(r[1]), Merge == "union" ? Uf.FindNaive(r[2]) : r[2], Timestamp }).ToArray());
            Table.Merge(State);
            State.Flush();
            Uf.Merge(State);
            Timestamp++;
        }

        /// <summary>
        /// Rebuilds the table to a fixpoint: rebuild, merge, merge the unions the merges staged, until nothing changes.
        /// </summary>
        /// <param name="forceFull">Whether to name the union-find by a fresh id on every call, so that the table's
        /// tracker sees the whole union-find as new and takes the full path whenever it is large enough.</param>
        /// <returns>The number of rebuild calls.</returns>
        public int RebuildToFixpoint(bool forceFull)
        {
            var calls = 0;
            while (true)
            {
                var provider = forceFull ? new TableId(freshProvider++) : UfId;
                var changed = Table.ApplyRebuild(provider, Uf, Timestamp, State);
                Table.Merge(State);
                State.Flush();
                var unions = Uf.Merge(State).Added;
                Timestamp++;
                calls++;
                if (changed == false && unions == false)
                    return calls;

                if (calls > 50)
                    throw new InvalidOperationException("The rebuild loop did not reach a fixpoint.");
            }
        }

    }

}
