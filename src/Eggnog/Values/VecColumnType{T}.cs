using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The column type of <see cref="Vec{T}"/>: egglog's <c>Vec</c> sort (src/sort/vec.rs). A vector's cell is the id of
    /// its <see cref="Relations.VecContainer"/>, interned in the graph's container environment with an id from the graph's
    /// id counter, as egglog's are; the engine column is an id column. A vector of values is never rebuilt and its id
    /// never merges, so equal vectors have equal cells in every scope, and the library treats it as a value. A vector of
    /// classes, or of containers of them, is egglog's eq-sort container (<see cref="ColumnType.HoldsClasses"/>): rebuilt
    /// against the union-find, its id merged with another's when their elements become equal, and interned only at the
    /// root (decisions row 9).
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal sealed class VecColumnType<T> : ColumnType<Vec<T>>, IContainerColumnType where T : notnull
    {

        readonly ColumnType<T> element;
        readonly bool holdsClasses;
        readonly Relations.ContainerEnv<Relations.VecContainer> env;
        readonly Relations.CounterSet counters;
        readonly Relations.VecSort sort;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The type's name.</param>
        /// <param name="element">The column type of the elements.</param>
        /// <param name="env">The graph's vector environment, registered as holding e-class ids when the elements are
        /// classes or hold them.</param>
        /// <param name="counters">The graph's counters, from which the environment mints ids.</param>
        public VecColumnType(
            string name,
            ColumnType<T> element,
            Relations.ContainerEnv<Relations.VecContainer> env,
            Relations.CounterSet counters)
            : base(name, ColumnKind.Value)
        {
            this.element = element;
            holdsClasses = element.InvolvesClasses;
            this.env = env;
            this.counters = counters;
            sort = new Relations.VecSort(name, element.ExtractionSort);
        }

        /// <inheritdoc />
        internal override bool HoldsClasses => holdsClasses;

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => Relations.ColumnTy.Id;

        /// <inheritdoc />
        public IReadOnlyList<ColumnType> ElementTypes => [element];

        /// <inheritdoc />
        internal override bool IsOrdered => element.IsOrdered;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <summary>
        /// Encodes a vector: its elements in order, then the container, interned without an execution state from the
        /// graph's id counter, as egglog's <c>vec-of</c> mints its container's id after evaluating its arguments.
        /// Thread-safe, from any scope.
        /// </summary>
        /// <param name="value">The vector.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="ArgumentNullException">The vector is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">The vector holds classes, which encode in a scope
        /// (<see cref="EncodeIn"/>).</exception>
        /// <exception cref="OverflowException">The graph has no ids left.</exception>
        internal override uint Encode(Vec<T> value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (holdsClasses)
                throw ContainerClasses.NeedScope(Name);

            Span<uint> cells = value.Count <= 64 ? stackalloc uint[value.Count] : new uint[value.Count];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = element.Encode(value[i]);

            return env.GetOrInsertShared(new Relations.VecContainer(false, cells), counters);
        }

        /// <summary>
        /// Encodes a vector a primitive returned in <paramref name="scope"/>: for a vector of classes, its classes
        /// usable there and the container interned at the root, as egglog's primitive registers its container through the
        /// execution state, or in a child the one the root holds; else as <see cref="Encode"/>.
        /// </summary>
        /// <param name="value">The vector.</param>
        /// <param name="scope">The scope the call runs in.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="InvalidOperationException">The vector holds classes, and a class is not usable in the scope,
        /// or the scope is a child and the root holds no such vector.</exception>
        internal override uint EncodeIn(Vec<T> value, Scope? scope)
        {
            if (holdsClasses == false)
                return Encode(value);

            ArgumentNullException.ThrowIfNull(value);
            var cells = new uint[value.Count];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = element.EncodeIn(value[i], scope);

            return ContainerClasses.Intern(env, new Relations.VecContainer(true, cells), counters, Name, scope);
        }

        /// <inheritdoc />
        internal override uint EncodeCanonical(Vec<T> value, Scope scope, Func<uint, uint> find, ScopeOrigin? openBatch)
        {
            if (holdsClasses == false)
                return Encode(value);

            ArgumentNullException.ThrowIfNull(value);
            var cells = new uint[value.Count];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = element.EncodeCanonical(value[i], scope, find, openBatch);

            return find(ContainerClasses.Intern(env, new Relations.VecContainer(true, cells), counters, Name, scope));
        }

        /// <inheritdoc />
        internal override Vec<T> Canonical(Vec<T> value, Scope scope, Func<uint, uint> find)
        {
            if (holdsClasses == false)
                return value;

            ArgumentNullException.ThrowIfNull(value);
            var items = new T[value.Count];
            for (var i = 0; i < items.Length; i++)
                items[i] = element.Canonical(value[i], scope, find);

            return Vec<T>.Own(items);
        }

        /// <inheritdoc />
        internal override bool TryEncodeExisting(Vec<T> value, out uint cell)
        {
            ArgumentNullException.ThrowIfNull(value);
            cell = 0;
            Span<uint> cells = value.Count <= 64 ? stackalloc uint[value.Count] : new uint[value.Count];
            for (var i = 0; i < cells.Length; i++)
                if (element.TryEncodeExisting(value[i], out cells[i]) == false)
                    return false;

            return env.TryGetId(new Relations.VecContainer(holdsClasses, cells), out cell);
        }

        /// <inheritdoc />
        internal override bool TryEncodeCanonical(Vec<T> value, Scope scope, Func<uint, uint> find, out uint cell)
        {
            if (holdsClasses == false)
                return TryEncodeExisting(value, out cell);

            ArgumentNullException.ThrowIfNull(value);
            cell = 0;
            var cells = new uint[value.Count];
            for (var i = 0; i < cells.Length; i++)
                if (element.TryEncodeCanonical(value[i], scope, find, out cells[i]) == false)
                    return false;

            if (env.TryGetId(new Relations.VecContainer(true, cells), out var id) == false)
                return false;

            cell = find(id);
            return true;
        }

        /// <summary>
        /// Decodes a vector's cell. Takes one lock of the container environment, and more for nested containers.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="scope">The scope serving the call.</param>
        /// <returns>The vector.</returns>
        /// <exception cref="InvalidOperationException">No vector has the cell.</exception>
        internal override Vec<T> Decode(uint cell, Scope? scope)
        {
            var vec = Read(cell);
            var items = new T[vec.Length];
            for (var i = 0; i < items.Length; i++)
                items[i] = element.Decode(vec.Values[i], scope);

            return Vec<T>.Own(items);
        }

        /// <summary>
        /// Formats a vector as egglog reconstructs one: <c>(vec-empty)</c> or <c>(vec-of a b)</c> (vec.rs:175-187).
        /// </summary>
        /// <param name="value">The vector.</param>
        /// <returns>The literal.</returns>
        internal override string FormatValue(Vec<T> value)
        {
            if (value.Count == 0)
                return "(vec-empty)";

            var parts = new string[value.Count];
            for (var i = 0; i < parts.Length; i++)
                parts[i] = element.FormatValue(value[i]);

            return "(vec-of " + string.Join(" ", parts) + ")";
        }

        /// <inheritdoc />
        internal override string DebugValue(Vec<T> value) => FormatValue(value);

        /// <summary>
        /// Formats a vector's cell as <see cref="FormatValue"/> formats the vector, through its elements' cells, so that a
        /// vector of classes formats without a scope.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <returns>The literal.</returns>
        internal override string FormatCell(uint cell)
        {
            var vec = Read(cell);
            if (vec.Length == 0)
                return "(vec-empty)";

            var parts = new string[vec.Length];
            for (var i = 0; i < parts.Length; i++)
                parts[i] = element.FormatCell(vec.Values[i]);

            return "(vec-of " + string.Join(" ", parts) + ")";
        }

        /// <summary>
        /// Compares two vectors lexicographically over their elements' content order, a shorter prefix first. Never
        /// interns.
        /// </summary>
        /// <param name="x">One vector.</param>
        /// <param name="y">The other vector.</param>
        /// <returns>The order.</returns>
        internal override int CompareValues(Vec<T> x, Vec<T> y)
        {
            for (var i = 0; i < x.Count && i < y.Count; i++)
            {
                var order = element.CompareValues(x[i], y[i]);
                if (order != 0)
                    return order;
            }

            return x.Count.CompareTo(y.Count);
        }

        /// <summary>
        /// Compares two vectors' cells as <see cref="CompareValues"/> compares the vectors, through their elements' cells.
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
            for (var i = 0; i < left.Length && i < right.Length; i++)
            {
                var order = element.CompareCells(left.Values[i], right.Values[i]);
                if (order != 0)
                    return order;
            }

            return left.Length.CompareTo(right.Length);
        }

        /// <inheritdoc />
        internal override bool ValuesEqual(Vec<T> x, Vec<T> y)
        {
            if (x.Count != y.Count)
                return false;

            for (var i = 0; i < x.Count; i++)
                if (element.ValuesEqual(x[i], y[i]) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public void AppendElements(uint cell, List<(ColumnType Type, uint Cell)> destination)
        {
            foreach (var value in Read(cell).Values)
                destination.Add((element, value));
        }

        /// <inheritdoc />
        public void AppendDistinctElements(uint cell, List<(ColumnType Type, uint Cell)> destination) => AppendElements(cell, destination);

        /// <inheritdoc />
        public bool RebuildsInOrder => element is IContainerColumnType { RebuildsInOrder: true };

        /// <inheritdoc />
        public long[] Content(uint cell)
        {
            var values = Read(cell).Values;
            var content = new long[values.Length];
            for (var i = 0; i < content.Length; i++)
                content[i] = values[i];

            return content;
        }

        /// <inheritdoc />
        public long[] Rebuild(long[] content, Func<uint, uint> find)
        {
            if (holdsClasses == false)
                return content;

            var rebuilt = new long[content.Length];
            for (var i = 0; i < rebuilt.Length; i++)
                rebuilt[i] = find((uint)content[i]);

            return rebuilt;
        }

        /// <inheritdoc />
        public string CanonicalKey(uint cell, Func<ColumnType, uint, string> part)
        {
            var parts = new List<string>();
            foreach (var element in Read(cell).Values)
                parts.Add(part(this.element, element));

            return "[" + string.Join(" ", parts) + "]";
        }

        /// <inheritdoc />
        /// <remarks><c>vec-of</c>, as egglog's <c>serialized_name</c> (vec.rs:189-191).</remarks>
        public string SerializedName(uint cell) => "vec-of";

        /// <summary>
        /// Gets the cells of a vector's elements, in order, without decoding them.
        /// </summary>
        /// <param name="cell">The vector's cell.</param>
        /// <returns>The cells.</returns>
        /// <exception cref="InvalidOperationException">No vector has the cell.</exception>
        internal uint[] ElementCells(uint cell) => Read(cell).Values.ToArray();

        /// <summary>
        /// Reads the container of a cell.
        /// </summary>
        /// <exception cref="InvalidOperationException">No vector has the cell.</exception>
        Relations.VecContainer Read(uint cell)
        {
            if (env.TryGetContainer(cell, out var vec) == false)
                throw new InvalidOperationException($"No {Name} has cell {cell}.");

            return vec;
        }

    }

}
