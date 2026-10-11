using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// egglog's <c>basic_math_egraph</c> fixture (core-relations/src/tests.rs:953-1010), ported from the test project's
    /// <c>Execution/ExecutionMathEgraph.cs</c>: a database with a union-find, <c>num(value, id, ts)</c> and
    /// <c>add(x, y, id, ts)</c>, each sorted by its timestamp and merged with <see cref="UnionTakeNewMerge"/>, and an id
    /// counter. The tables the two <see cref="RuleSetRunBenchmarks"/> programs run on.
    /// </summary>
    /// <remarks>
    /// The test fixture builds its tables with <see cref="ParallelHeuristics.Serial"/> in every mode, because its
    /// databases hold a few dozen rows; at the benchmark sizes the tables take the mode's heuristics, so the parallel
    /// mode also merges large tables in parallel, as a parallel host would.
    /// </remarks>
    internal sealed class MathEgraph
    {

        /// <summary>
        /// Initializes the fixture: an empty database for <paramref name="mode"/> holding the three tables.
        /// </summary>
        /// <param name="mode">The execution mode.</param>
        public MathEgraph(RunMode mode)
        {
            var parallelism = RootParallelism.For(mode);
            Db = RootParallelism.CreateDatabase(mode);
            Uf = Db.AddTable(new DisplacedTable(), name: "uf");
            IdCounter = Db.AddCounter();
            Num = Db.AddTable(
                new SortedWritesTable(1, 3, new ColumnId(2), ReadOnlySpan<ColumnId>.Empty, new UnionTakeNewMerge(Uf, 1, 2), parallelism),
                [Uf],
                name: "num");
            Add = Db.AddTable(
                new SortedWritesTable(2, 4, new ColumnId(3), ReadOnlySpan<ColumnId>.Empty, new UnionTakeNewMerge(Uf, 2, 3), parallelism),
                [Uf],
                name: "add");
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
        /// Gets <c>num(value, id, ts)</c>.
        /// </summary>
        public TableId Num { get; }

        /// <summary>
        /// Gets <c>add(x, y, id, ts)</c>.
        /// </summary>
        public TableId Add { get; }

        /// <summary>
        /// Gets the id counter.
        /// </summary>
        public CounterId IdCounter { get; }

        /// <summary>
        /// Gets the union-find.
        /// </summary>
        public DisplacedTable UnionFind => (DisplacedTable)Db.GetTable(Uf);

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/>.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The canonical id.</returns>
        public uint Find(uint id) => UnionFind.FindNaive(id);

    }

}
