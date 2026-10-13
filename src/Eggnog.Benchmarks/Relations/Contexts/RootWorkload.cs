using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A root for the per-child benchmarks of colored contexts: a <see cref="Store"/> whose root holds
    /// <see cref="Rows"/> ids, <c>f(x) → v</c> for each (a lattice function, merged by the larger value) and
    /// <c>r(x, x+1)</c> for each (a relation), loaded in one batch, and frozen unless asked otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every id is named by exactly one <c>f</c> row and two <c>r</c> rows, so a child's work over a fixed set of ids
    /// touches a fixed number of root rows whatever <see cref="Rows"/> is: what "flat" means for the per-child cost.
    /// </para>
    /// <para>
    /// A frozen root built its rebuild indexes at its freeze and shares them with every child, and its children may run
    /// concurrently. A root that is not frozen settles and brings its rebuild indexes up to date when it creates a
    /// child (root work, covering the root's writes since it last did so, not a per-child cost); its children share its
    /// thread domain and run one at a time.
    /// </para>
    /// </remarks>
    internal sealed class RootWorkload
    {

        /// <summary>
        /// Builds the root, and freezes it when <paramref name="freeze"/> is set.
        /// </summary>
        /// <param name="rows">The number of ids, and of rows of each table.</param>
        /// <param name="freeze">Whether to freeze the root.</param>
        public RootWorkload(int rows, bool freeze = true)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1000);
            Rows = rows;
            Store = new Store(ParallelHeuristics.Serial);
            var keyColumn = new ColumnId(0);
            F = Store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [keyColumn], new MaxMerge(new ColumnId(1)), ParallelHeuristics.Serial), name: "f");
            R = Store.AddTable(new SortedWritesTable(2, 3, new ColumnId(2), [keyColumn, new ColumnId(1)], new MinMerge(new ColumnId(2)), ParallelHeuristics.Serial), name: "r");
            var root = Store.Root;
            Ids = new uint[rows];
            for (var i = 0; i < rows; i++)
                Ids[i] = root.NewId();

            root.Write(this, static (state, workload) =>
            {
                var ids = workload.Ids;
                for (var i = 0; i < ids.Length; i++)
                {
                    state.StageInsert(workload.F, [ids[i], (uint)(i % 7), state.Timestamp]);
                    state.StageInsert(workload.R, [ids[i], ids[(i + 1) % ids.Length], state.Timestamp]);
                }

                return 0;
            });

            if (freeze)
                root.Freeze();
        }

        /// <summary>
        /// Gets the number of ids, and of rows of each table.
        /// </summary>
        public int Rows { get; }

        /// <summary>
        /// Gets the store.
        /// </summary>
        public Store Store { get; }

        /// <summary>
        /// Gets <c>f(x) → v</c>.
        /// </summary>
        public TableId F { get; }

        /// <summary>
        /// Gets <c>r(x, y)</c>.
        /// </summary>
        public TableId R { get; }

        /// <summary>
        /// Gets the root's ids, ascending.
        /// </summary>
        public uint[] Ids { get; }

    }

}
