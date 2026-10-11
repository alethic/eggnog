using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The database <see cref="ActionLookupBenchmarks"/> runs its rule on, laid out as egglog's bridge lays one out:
    /// a union-find, the constructors <c>leaf(i64) -&gt; id</c> and <c>combine(id, id) -&gt; id</c> merged with
    /// <see cref="UnionIdMerge"/>, the function <c>weight(id) -&gt; i64</c>, the function <c>best(i64) -&gt; id</c>
    /// merged with <see cref="FunctionMerge"/> (<c>:merge (combine old new)</c>), the relation <c>driver(i64, i64)</c>
    /// the rule scans, an id counter and a timestamp counter. Each table is <c>[columns..., ts]</c>, sorted by the
    /// timestamp.
    /// </summary>
    /// <remarks>
    /// <c>best</c> declares <c>combine</c> a read dependency, so <c>combine</c> merges in an earlier stratum and stays
    /// readable while <c>best</c>'s merge predicts into it. <see cref="ActionLookupWorkload"/> fills the tables and
    /// builds the rule.
    /// </remarks>
    internal sealed class ActionLookupProgram
    {

        /// <summary>
        /// Initializes an empty database for <paramref name="mode"/>.
        /// </summary>
        /// <param name="mode">The execution mode: the database's and the tables' parallelism, and the executor.</param>
        public ActionLookupProgram(RunMode mode)
        {
            var parallelism = RootParallelism.For(mode);
            Db = RootParallelism.CreateDatabase(mode);
            Uf = Db.AddTable(new DisplacedTable(), name: "$uf");
            Ids = Db.AddCounter();
            Clock = Db.AddCounter();
            Leaf = Db.AddTable(
                new SortedWritesTable(
                    1,
                    3,
                    new ColumnId(2),
                    [new ColumnId(1)],
                    new UnionIdMerge(new ColumnId(1), Uf, new ColumnId(2)),
                    parallelism),
                [],
                [Uf],
                "leaf");
            Combine = Db.AddTable(
                new SortedWritesTable(
                    2,
                    4,
                    new ColumnId(3),
                    [new ColumnId(0), new ColumnId(1), new ColumnId(2)],
                    new UnionIdMerge(new ColumnId(2), Uf, new ColumnId(3)),
                    parallelism),
                [],
                [Uf],
                "combine");
            Weight = Db.AddTable(
                new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(0)], new MinMerge(new ColumnId(1)), parallelism),
                name: "weight");
            Best = Db.AddTable(
                new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(1)], new FunctionMerge(Combine, Ids, Clock, 1), parallelism),
                [Combine],
                [Combine],
                "best");
            Driver = Db.AddTable(
                new SortedWritesTable(2, 3, new ColumnId(2), ReadOnlySpan<ColumnId>.Empty, KeepCurrentMerge.Instance, parallelism),
                name: "driver");
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
        /// Gets the id counter.
        /// </summary>
        public CounterId Ids { get; }

        /// <summary>
        /// Gets the timestamp counter.
        /// </summary>
        public CounterId Clock { get; }

        /// <summary>
        /// Gets <c>leaf(k, id, ts)</c>.
        /// </summary>
        public TableId Leaf { get; }

        /// <summary>
        /// Gets <c>combine(a, b, id, ts)</c>.
        /// </summary>
        public TableId Combine { get; }

        /// <summary>
        /// Gets <c>weight(id, w, ts)</c>.
        /// </summary>
        public TableId Weight { get; }

        /// <summary>
        /// Gets <c>best(k, id, ts)</c>.
        /// </summary>
        public TableId Best { get; }

        /// <summary>
        /// Gets <c>driver(a, b, ts)</c>.
        /// </summary>
        public TableId Driver { get; }

        /// <summary>
        /// Gets the clock's current value.
        /// </summary>
        public uint Timestamp => checked((uint)Db.ReadCounter(Clock));

    }

}
