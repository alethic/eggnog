using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's <c>basic_math_egraph</c> fixture (core-relations/src/tests.rs:953-1010): a union-find, <c>num(value, id,
    /// ts)</c> keyed by the value and <c>add(x, y, id, ts)</c> keyed by the operands, both sorted by their timestamp and
    /// merged by staging a union and taking the incoming row, and an id counter.
    /// </summary>
    sealed class MathEgraph
    {

        /// <summary>
        /// Initializes the fixture. Unlike egglog's, the tables are named, so symbol maps can be checked.
        /// </summary>
        /// <param name="rebuildIds">Whether the tables declare their id columns as rebuild columns (egglog's fixture
        /// declares none).</param>
        public MathEgraph(bool rebuildIds = false)
        {
            Db = new Database(ParallelHeuristics.Serial);
            Uf = Db.AddTable(new DisplacedTable(), name: "uf");
            ReadOnlySpan<ColumnId> numIds = rebuildIds ? [new ColumnId(1)] : [];
            ReadOnlySpan<ColumnId> addIds = rebuildIds ? [new ColumnId(0), new ColumnId(1), new ColumnId(2)] : [];
            IdCounter = Db.AddCounter();
            Num = Db.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), numIds, new UnionTakeNewMerge(Uf, 1, 2), ParallelHeuristics.Serial), [Uf], name: "num");
            Add = Db.AddTable(new SortedWritesTable(2, 4, new ColumnId(3), addIds, new UnionTakeNewMerge(Uf, 2, 3), ParallelHeuristics.Serial), [Uf], name: "add");
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
