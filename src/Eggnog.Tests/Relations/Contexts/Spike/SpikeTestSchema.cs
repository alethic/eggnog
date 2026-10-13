using System;
using System.Collections.Generic;
using System.Linq;

using Spike = Eggnog.Storage;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The tables the context differentials run over (colored-contexts spec §17 M9.4, X32), ported from the spike's
    /// <c>Harness/TestSchema</c>: a binary and a unary constructor, a lattice, a relation mixing ids with values, and a
    /// constructor keyed by a value alone. Defined in an engine <see cref="Store"/> and, when given, in a spike store.
    /// </summary>
    /// <remarks>
    /// Engine shapes, each with a timestamp column last: <c>pair(id, id) → id</c>, <c>wrap(id) → id</c> and
    /// <c>named(value) → id</c> merged by <see cref="UnionIdMerge"/> (they write the union-find, a write dependency);
    /// <c>low(id) → value</c> merged by the smaller value (<see cref="MinMerge"/>); <c>tag(id, value)</c>, a relation whose
    /// re-assertion keeps the older timestamp (<see cref="MinMerge"/> on the timestamp). Every merge is a lattice merge, so
    /// children may write every table (decision 2).
    /// </remarks>
    sealed class SpikeTestSchema
    {

        /// <summary>
        /// Defines the tables in <paramref name="store"/> and, if given, in <paramref name="spike"/>.
        /// </summary>
        /// <param name="store">The engine store.</param>
        /// <param name="spike">The spike's store, or <c>null</c>.</param>
        public SpikeTestSchema(Store store, Spike.Store? spike)
        {
            ArgumentNullException.ThrowIfNull(store);
            Store = store;
            var uf = store.UnionFindTable;
            Pair = new SpikeTable("pair", store.AddTable(StoreTests.Function(store, 2), [], [uf], "pair"), spike?.DefineTable(Spike.TableSchema.Constructor("pair", Spike.ColumnKind.Id, Spike.ColumnKind.Id)), [true, true], true, null);
            Wrap = new SpikeTable("wrap", store.AddTable(StoreTests.Function(store, 1), [], [uf], "wrap"), spike?.DefineTable(Spike.TableSchema.Constructor("wrap", Spike.ColumnKind.Id)), [true], true, null);
            Low = new SpikeTable("low", store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(0)], new MinMerge(new ColumnId(1)), ParallelHeuristics.Serial), name: "low"), spike?.DefineTable(Spike.TableSchema.Lattice("low", Math.Min, Spike.ColumnKind.Id)), [true], false, Math.Min);
            Tag = new SpikeTable("tag", store.AddTable(new SortedWritesTable(2, 3, new ColumnId(2), [new ColumnId(0)], new MinMerge(new ColumnId(2)), ParallelHeuristics.Serial), name: "tag"), spike?.DefineTable(Spike.TableSchema.Relation("tag", Spike.ColumnKind.Id, Spike.ColumnKind.Value)), [true, false], null, null);
            Named = new SpikeTable("named", store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(1)], new UnionIdMerge(new ColumnId(1), uf, new ColumnId(2)), ParallelHeuristics.Serial), [], [uf], "named"), spike?.DefineTable(Spike.TableSchema.Constructor("named", Spike.ColumnKind.Value)), [false], true, null);
            Bits = new SpikeTable("bits", store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [new ColumnId(0)], new OrMerge(1), ParallelHeuristics.Serial), name: "bits"), spike?.DefineTable(Spike.TableSchema.Lattice("bits", (a, b) => a | b, Spike.ColumnKind.Id)), [true], false, (a, b) => a | b);
            Tables = [Pair, Wrap, Low, Tag, Named, Bits];
        }

        /// <summary>
        /// Gets the engine store.
        /// </summary>
        public Store Store { get; }

        /// <summary>
        /// Gets <c>pair(id, id) → id</c>.
        /// </summary>
        public SpikeTable Pair { get; }

        /// <summary>
        /// Gets <c>wrap(id) → id</c>.
        /// </summary>
        public SpikeTable Wrap { get; }

        /// <summary>
        /// Gets <c>low(id) → value</c>, merged by the minimum.
        /// </summary>
        public SpikeTable Low { get; }

        /// <summary>
        /// Gets the relation <c>tag(id, value)</c>.
        /// </summary>
        public SpikeTable Tag { get; }

        /// <summary>
        /// Gets <c>named(value) → id</c>.
        /// </summary>
        public SpikeTable Named { get; }

        /// <summary>
        /// Gets <c>bits(id) → value</c>, a lattice joining bit sets (<see cref="OrMerge"/>): its merge output is in general
        /// neither the current nor the incoming row, which the other tables' merges cannot show.
        /// </summary>
        public SpikeTable Bits { get; }

        /// <summary>
        /// Gets every table, in definition order.
        /// </summary>
        public IReadOnlyList<SpikeTable> Tables { get; }

        /// <summary>
        /// Gets the table with engine id <paramref name="id"/>.
        /// </summary>
        public SpikeTable this[TableId id] => Tables.First(t => t.Id == id);

    }

}
