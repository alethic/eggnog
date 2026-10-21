using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The column type of <see cref="Pair{T1, T2}"/>: egglog's <c>Pair</c> sort (src/sort/pair.rs) over value element
    /// types. A pair's cell is the id of its <see cref="Relations.PairContainer"/>, interned in the graph's container
    /// environment with an id from the graph's id counter, as egglog's are; the engine column is an id column, as egglog's
    /// <c>ContainerSortImpl::column_ty</c>. A pair of values is never rebuilt and its id never merges, so equal pairs have
    /// equal cells in every scope, as any value's do, and the library treats it as a value.
    /// </summary>
    /// <typeparam name="T1">The type of the first element.</typeparam>
    /// <typeparam name="T2">The type of the second element.</typeparam>
    internal sealed class PairColumnType<T1, T2> : ColumnType<Pair<T1, T2>>, IContainerColumnType
        where T1 : notnull
        where T2 : notnull
    {

        readonly ColumnType<T1> first;
        readonly ColumnType<T2> second;
        readonly Relations.ContainerEnv<Relations.PairContainer> env;
        readonly Relations.CounterSet counters;
        readonly Relations.PairSort sort;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The type's name.</param>
        /// <param name="first">The column type of the first element; a value type.</param>
        /// <param name="second">The column type of the second element; a value type.</param>
        /// <param name="env">The graph's pair environment, registered as holding no e-class ids.</param>
        /// <param name="counters">The graph's counters, from which the environment mints ids.</param>
        public PairColumnType(
            string name,
            ColumnType<T1> first,
            ColumnType<T2> second,
            Relations.ContainerEnv<Relations.PairContainer> env,
            Relations.CounterSet counters)
            : base(name, ColumnKind.Value)
        {
            this.first = first;
            this.second = second;
            this.env = env;
            this.counters = counters;
            sort = new Relations.PairSort(name, first.ExtractionSort, second.ExtractionSort);
        }

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => Relations.ColumnTy.Id;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <summary>
        /// Encodes a pair: its elements, then the container, interned without an execution state from the graph's id
        /// counter, as egglog's <c>pair</c> mints its container's id after evaluating its arguments. Thread-safe, from any
        /// scope; the pair stays interned after the scope is gone.
        /// </summary>
        /// <param name="value">The pair.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="ArgumentException">The pair is a default one, whose elements are <c>null</c>.</exception>
        /// <exception cref="OverflowException">The graph has no ids left.</exception>
        internal override uint Encode(Pair<T1, T2> value)
        {
            CheckElements(value);
            var a = first.Encode(value.First);
            var b = second.Encode(value.Second);
            return env.GetOrInsertShared(new Relations.PairContainer(false, false, a, b), counters);
        }

        /// <inheritdoc />
        internal override bool TryEncodeExisting(Pair<T1, T2> value, out uint cell)
        {
            CheckElements(value);
            cell = 0;
            if (first.TryEncodeExisting(value.First, out var a) == false || second.TryEncodeExisting(value.Second, out var b) == false)
                return false;

            return env.TryGetId(new Relations.PairContainer(false, false, a, b), out cell);
        }

        /// <summary>
        /// Decodes a pair's cell. Takes one lock of the container environment.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="scope">The scope serving the call.</param>
        /// <returns>The pair.</returns>
        /// <exception cref="InvalidOperationException">No pair has the cell.</exception>
        internal override Pair<T1, T2> Decode(uint cell, Scope? scope)
        {
            var pair = Read(cell);
            return new(first.Decode(pair.First, scope), second.Decode(pair.Second, scope));
        }

        /// <summary>
        /// Formats a pair as egglog reconstructs one: <c>(pair a b)</c> (pair.rs:135-144).
        /// </summary>
        /// <param name="value">The pair.</param>
        /// <returns>The literal.</returns>
        internal override string FormatValue(Pair<T1, T2> value) =>
            "(pair " + first.FormatValue(value.First) + " " + second.FormatValue(value.Second) + ")";

        /// <inheritdoc />
        internal override string DebugValue(Pair<T1, T2> value) => FormatValue(value);

        /// <summary>
        /// Compares two pairs lexicographically over their elements' content order. Never interns.
        /// </summary>
        /// <param name="x">One pair.</param>
        /// <param name="y">The other pair.</param>
        /// <returns>The order.</returns>
        internal override int CompareValues(Pair<T1, T2> x, Pair<T1, T2> y)
        {
            CheckElements(x);
            CheckElements(y);
            var order = first.CompareValues(x.First, y.First);
            return order != 0 ? order : second.CompareValues(x.Second, y.Second);
        }

        /// <summary>
        /// Compares two pairs' cells as <see cref="CompareValues"/> compares the pairs, through their elements' cells.
        /// </summary>
        /// <param name="x">One cell.</param>
        /// <param name="y">The other cell.</param>
        /// <returns>The order.</returns>
        internal override int CompareCells(uint x, uint y)
        {
            if (x == y)
                return 0;

            var left = Read(x);
            var right = Read(y);
            var order = first.CompareCells(left.First, right.First);
            return order != 0 ? order : second.CompareCells(left.Second, right.Second);
        }

        /// <inheritdoc />
        internal override bool ValuesEqual(Pair<T1, T2> x, Pair<T1, T2> y) =>
            first.ValuesEqual(x.First, y.First) && second.ValuesEqual(x.Second, y.Second);

        /// <inheritdoc />
        public void AppendElements(uint cell, List<(ColumnType Type, uint Cell)> destination)
        {
            var pair = Read(cell);
            destination.Add((first, pair.First));
            destination.Add((second, pair.Second));
        }

        /// <inheritdoc />
        /// <remarks>The pair sort's name, as egglog's <c>serialized_name</c> (pair.rs:146-148).</remarks>
        public string SerializedName(uint cell) => Name;

        /// <summary>
        /// Reads the container of a cell.
        /// </summary>
        /// <exception cref="InvalidOperationException">No pair has the cell.</exception>
        Relations.PairContainer Read(uint cell)
        {
            if (env.TryGetContainer(cell, out var pair) == false)
                throw new InvalidOperationException($"No {Name} has cell {cell}.");

            return pair;
        }

        /// <summary>
        /// Refuses a default pair, whose elements are <c>null</c>.
        /// </summary>
        /// <exception cref="ArgumentException">An element is <c>null</c>.</exception>
        static void CheckElements(Pair<T1, T2> value)
        {
            if (value.First is null || value.Second is null)
                throw new ArgumentException("A default pair has no elements; make one with new Pair(first, second).", nameof(value));
        }

    }

}
