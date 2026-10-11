using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's <c>basic_math_egraph</c> fixture (core-relations/src/tests.rs:953-1010), as <see cref="MathEgraph"/>, on a
    /// database that runs rule sets in a given executor mode (see <see cref="ExecutionTestUtil"/>).
    /// </summary>
    sealed class ExecutionMathEgraph
    {

        /// <summary>
        /// Initializes the fixture.
        /// </summary>
        /// <param name="mode">The executor mode.</param>
        public ExecutionMathEgraph(string mode)
        {
            Db = ExecutionTestUtil.NewDatabase(mode);
            Uf = Db.AddTable(new DisplacedTable(), name: "uf");
            IdCounter = Db.AddCounter();
            Num = Db.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), ReadOnlySpan<ColumnId>.Empty, new UnionTakeNewMerge(Uf, 1, 2), ParallelHeuristics.Serial), [Uf], name: "num");
            Add = Db.AddTable(new SortedWritesTable(2, 4, new ColumnId(3), ReadOnlySpan<ColumnId>.Empty, new UnionTakeNewMerge(Uf, 2, 3), ParallelHeuristics.Serial), [Uf], name: "add");
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
        /// Gets the canonical id of <paramref name="id"/>.
        /// </summary>
        public uint Find(uint id) => ((DisplacedTable)Db.GetTable(Uf)).FindNaive(id);

    }

}
