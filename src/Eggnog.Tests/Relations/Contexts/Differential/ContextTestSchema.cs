using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The tables the context differentials run over: a binary and a unary constructor, a lattice, a relation mixing
    /// ids with values, and a constructor keyed by a value alone, defined in an engine <see cref="Store"/>.
    /// </summary>
    /// <remarks>
    /// Engine shapes, each with a timestamp column last: <c>pair(id, id) → id</c>, <c>wrap(id) → id</c> and
    /// <c>named(value) → id</c> merged by <see cref="UnionIdMerge"/> (they write the union-find, a write dependency);
    /// <c>low(id) → value</c> merged by the smaller value (<see cref="MinMerge"/>); <c>tag(id, value)</c>, a relation
    /// whose re-assertion keeps the older timestamp (<see cref="MinMerge"/> on the timestamp). Every merge is a lattice
    /// merge, so children may write every table.
    /// </remarks>
    sealed class ContextTestSchema
    {

        /// <summary>
        /// Defines the tables in <paramref name="store"/>.
        /// </summary>
        /// <param name="store">The engine store.</param>
        public ContextTestSchema(Store store)
        {
            ArgumentNullException.ThrowIfNull(store);
            Store = store;
            var uf = store.UnionFindTable;
            Pair = new LogicalTable("pair", store.AddTable(StoreTests.Function(store, 2), [], [uf], "pair"), [true, true], true, null);
            Wrap = new LogicalTable("wrap", store.AddTable(StoreTests.Function(store, 1), [], [uf], "wrap"), [true], true, null);
            Low = new LogicalTable("low", store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(0)], new MinMerge(new ColumnId(1)), ParallelHeuristics.Serial), name: "low"), [true], false, Math.Min);
            Tag = new LogicalTable("tag", store.AddTable(new SortedWritesTable(2, 3, new ColumnId(2), [new ColumnId(0)], new MinMerge(new ColumnId(2)), ParallelHeuristics.Serial), name: "tag"), [true, false], null, null);
            Named = new LogicalTable("named", store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(1)], new UnionIdMerge(new ColumnId(1), uf, new ColumnId(2)), ParallelHeuristics.Serial), [], [uf], "named"), [false], true, null);
            Bits = new LogicalTable("bits", store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(0)], new OrMerge(1), ParallelHeuristics.Serial), name: "bits"), [true], false, (a, b) => a | b);
            Tables = [Pair, Wrap, Low, Tag, Named, Bits];
        }

        /// <summary>
        /// Gets the engine store.
        /// </summary>
        public Store Store { get; }

        /// <summary>
        /// Gets <c>pair(id, id) → id</c>.
        /// </summary>
        public LogicalTable Pair { get; }

        /// <summary>
        /// Gets <c>wrap(id) → id</c>.
        /// </summary>
        public LogicalTable Wrap { get; }

        /// <summary>
        /// Gets <c>low(id) → value</c>, merged by the minimum.
        /// </summary>
        public LogicalTable Low { get; }

        /// <summary>
        /// Gets the relation <c>tag(id, value)</c>.
        /// </summary>
        public LogicalTable Tag { get; }

        /// <summary>
        /// Gets <c>named(value) → id</c>.
        /// </summary>
        public LogicalTable Named { get; }

        /// <summary>
        /// Gets <c>bits(id) → value</c>, a lattice joining bit sets (<see cref="OrMerge"/>): its merge output is in
        /// general neither the current nor the incoming row, which the other tables' merges cannot show.
        /// </summary>
        public LogicalTable Bits { get; }

        /// <summary>
        /// Gets every table, in definition order.
        /// </summary>
        public IReadOnlyList<LogicalTable> Tables { get; }

        /// <summary>
        /// Gets the table with engine id <paramref name="id"/>.
        /// </summary>
        public LogicalTable this[TableId id] => Tables.First(t => t.Id == id);

    }

}
