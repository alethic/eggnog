using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The e-graph <see cref="DisplacedRebuildBenchmarks"/> unions and rebuilds, laid out as egglog's bridge lays one
    /// out: a <see cref="DisplacedTable"/> union-find, the constructors <c>pair(id, id) -&gt; id</c> and
    /// <c>wrap(id) -&gt; id</c> merged with <see cref="UnionIdMerge"/>, the lattice <c>low(id) -&gt; value</c> merged
    /// with <see cref="MinMerge"/>, and a timestamp counter. Each function table is <c>[columns..., ts]</c>, sorted by
    /// the timestamp, and rebuilds its id columns.
    /// </summary>
    /// <remarks>
    /// The constructors only write the union-find (a write dependency, so they share its stratum), as the bridge
    /// declares them. <see cref="TermGraphWorkload"/> fills the tables.
    /// </remarks>
    internal sealed class TermGraph
    {

        /// <summary>
        /// Initializes an empty e-graph for <paramref name="mode"/>.
        /// </summary>
        /// <param name="mode">The execution mode: the database's and the tables' parallelism.</param>
        public TermGraph(RunMode mode)
        {
            var parallelism = RootParallelism.For(mode);
            Db = RootParallelism.CreateDatabase(mode);
            Uf = Db.AddTable(new DisplacedTable(), name: "$uf");
            Clock = Db.AddCounter();
            Pair = Db.AddTable(
                new SortedWritesTable(
                    2,
                    4,
                    new ColumnId(3),
                    [new ColumnId(0), new ColumnId(1), new ColumnId(2)],
                    new UnionIdMerge(new ColumnId(2), Uf, new ColumnId(3)),
                    parallelism),
                [],
                [Uf],
                "pair");
            Wrap = Db.AddTable(
                new SortedWritesTable(
                    1,
                    3,
                    new ColumnId(2),
                    [new ColumnId(0), new ColumnId(1)],
                    new UnionIdMerge(new ColumnId(1), Uf, new ColumnId(2)),
                    parallelism),
                [],
                [Uf],
                "wrap");
            Low = Db.AddTable(
                new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(0)], new MinMerge(new ColumnId(1)), parallelism),
                name: "low");
            FunctionTables = [Pair, Wrap, Low];
        }

        /// <summary>
        /// Gets the database.
        /// </summary>
        public Database Db { get; }

        /// <summary>
        /// Gets the union-find table.
        /// </summary>
        public TableId Uf { get; }

        /// <summary>
        /// Gets <c>pair(x, y, id, ts)</c>.
        /// </summary>
        public TableId Pair { get; }

        /// <summary>
        /// Gets <c>wrap(x, id, ts)</c>.
        /// </summary>
        public TableId Wrap { get; }

        /// <summary>
        /// Gets <c>low(x, value, ts)</c>.
        /// </summary>
        public TableId Low { get; }

        /// <summary>
        /// Gets the timestamp counter: the bridge's clock, which <see cref="Database.Rebuild"/> reads and advances.
        /// </summary>
        public CounterId Clock { get; }

        /// <summary>
        /// Gets the function tables, in the order the rebuild visits them.
        /// </summary>
        public TableId[] FunctionTables { get; }

        /// <summary>
        /// Gets the union-find.
        /// </summary>
        public DisplacedTable UnionFind => (DisplacedTable)Db.GetTable(Uf);

        /// <summary>
        /// Gets the clock's current value: the timestamp writes made now carry.
        /// </summary>
        public uint Timestamp => checked((uint)Db.ReadCounter(Clock));

    }

}
