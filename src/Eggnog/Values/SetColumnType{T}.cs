using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The column type of <see cref="Set{T}"/>: egglog's <c>Set</c> sort (src/sort/set.rs). A set's cell is the id of
    /// its <see cref="Relations.SetContainer"/>, whose elements are in cell order as egglog's <c>BTreeSet</c>, interned
    /// in the graph's container environment with an id from the graph's id counter; the engine column is an id column.
    /// A set of values is never rebuilt and its id never merges, so equal sets have equal cells in every scope, and the
    /// library treats it as a value. A set of classes, or of containers of them, is egglog's eq-sort container
    /// (<see cref="ColumnType.HoldsClasses"/>): rebuilt as egglog's, elements that become one becoming one element, and
    /// interned at a writable root in the graph's environment and in a writable child in its own containers, under an
    /// id local to it (decisions row 9).
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal sealed class SetColumnType<T> : ColumnType<Set<T>>, IContainerColumnType where T : notnull
    {

        readonly ColumnType<T> element;
        readonly bool rebuild;
        readonly Relations.ContainerEnv<Relations.SetContainer> env;
        readonly Relations.CounterSet counters;
        readonly Relations.SetSort sort;

        /// <summary>
        /// The order of elements' cells through the root's view (<see cref="CellOrder"/>), made on first use, so that
        /// ordering a set's elements at the root allocates no comparison.
        /// </summary>
        Comparison<uint>? rootCellOrder;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The type's name.</param>
        /// <param name="element">The column type of the elements.</param>
        /// <param name="env">The graph's set environment, registered as holding e-class ids when the elements are
        /// classes or hold them.</param>
        /// <param name="counters">The graph's counters, from which the environment mints ids.</param>
        public SetColumnType(
            string name,
            ColumnType<T> element,
            Relations.ContainerEnv<Relations.SetContainer> env,
            Relations.CounterSet counters)
            : base(name, ColumnKind.Value)
        {
            this.element = element;
            rebuild = element.InvolvesClasses;
            this.env = env;
            this.counters = counters;
            sort = new Relations.SetSort(name, element.ExtractionSort);
        }

        /// <summary>
        /// Gets the column type of the elements.
        /// </summary>
        internal ColumnType<T> Element => element;

        /// <inheritdoc />
        internal override bool HoldsClasses => rebuild;

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => Relations.ColumnTy.Id;

        /// <inheritdoc />
        public IReadOnlyList<ColumnType> ElementTypes => [element];

        /// <inheritdoc />
        public Relations.ContainerEnvBase Environment => env;

        /// <inheritdoc />
        internal override bool IsOrdered => element.IsOrdered;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <summary>
        /// Encodes a set: its elements, then the container, its elements in cell order, interned without an execution
        /// state from the graph's id counter. Thread-safe, from any scope.
        /// </summary>
        /// <param name="value">The set.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="ArgumentNullException">The set is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Two elements the set keeps apart are one value to the element
        /// type.</exception>
        /// <exception cref="InvalidOperationException">The set holds classes, which encode in a scope
        /// (<see cref="EncodeIn"/>).</exception>
        /// <exception cref="OverflowException">The graph has no ids left.</exception>
        internal override uint Encode(Set<T> value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (rebuild)
                throw ContainerClasses.NeedScope(Name);

            var cells = new uint[value.Count];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = element.Encode(value[i]);

            return env.GetOrInsertShared(Collect(value, cells), counters);
        }

        /// <summary>
        /// Encodes a set a primitive returned in <paramref name="scope"/>: for a set of classes, its classes usable
        /// there and the container interned at a writable root or a writable child, which makes it in its own
        /// containers when its view does not hold it, or in a frozen scope the one it sees (its container view,
        /// <see cref="Scope.ContainerView"/>); else as <see cref="Encode"/>.
        /// </summary>
        /// <param name="value">The set.</param>
        /// <param name="scope">The scope the call runs in.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="InvalidOperationException">The set holds classes, and a class is not usable in the scope, or
        /// the scope is frozen and sees no such set.</exception>
        internal override uint EncodeIn(Set<T> value, Scope? scope)
        {
            if (rebuild == false)
                return Encode(value);

            ArgumentNullException.ThrowIfNull(value);
            var cells = new uint[value.Count];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = element.EncodeIn(value[i], scope);

            return ContainerClasses.Intern(env, Collect(value, cells), counters, Name, scope);
        }

        /// <inheritdoc />
        /// <remarks>Elements that are one class now become one, as egglog's rebuild makes them.</remarks>
        internal override uint EncodeCanonical(Set<T> value, Scope scope, Func<uint, uint> find, ScopeOrigin? openBatch)
        {
            if (rebuild == false)
                return Encode(value);

            ArgumentNullException.ThrowIfNull(value);
            var cells = new uint[value.Count];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = element.EncodeCanonical(value[i], scope, find, openBatch);

            return find(ContainerClasses.Intern(env, Collect(value, cells), counters, Name, scope));
        }

        /// <inheritdoc />
        internal override bool TryEncodeExisting(Set<T> value, Relations.ContainerView view, out uint cell)
        {
            ArgumentNullException.ThrowIfNull(value);
            cell = 0;
            var cells = new uint[value.Count];
            for (var i = 0; i < cells.Length; i++)
                if (element.TryEncodeExisting(value[i], view, out cells[i]) == false)
                    return false;

            return view.TryGetId(env, Collect(value, cells), out cell);
        }

        /// <inheritdoc />
        internal override bool TryEncodeCanonical(Set<T> value, Scope scope, Func<uint, uint> find, out uint cell)
        {
            if (rebuild == false)
                return TryEncodeExisting(value, scope.ContainerView, out cell);

            ArgumentNullException.ThrowIfNull(value);
            cell = 0;
            var cells = new uint[value.Count];
            for (var i = 0; i < cells.Length; i++)
                if (element.TryEncodeCanonical(value[i], scope, find, out cells[i]) == false)
                    return false;

            if (scope.ContainerView.TryGetId(env, Collect(value, cells), out var id) == false)
                return false;

            cell = find(id);
            return true;
        }

        /// <inheritdoc />
        internal override Set<T> Canonical(Set<T> value, Scope scope, Func<uint, uint> find)
        {
            if (rebuild == false)
                return value;

            ArgumentNullException.ThrowIfNull(value);
            var items = new List<T>(value.Count);
            foreach (var item in value)
                items.Add(element.Canonical(item, scope, find));

            return Set<T>.Create(items);
        }

        /// <summary>
        /// Makes the container of a set's cells, its elements in cell order: elements that are classes may become one, as
        /// egglog's rebuild makes them; elements of values the set keeps apart but the type stores as one are refused.
        /// </summary>
        /// <exception cref="ArgumentException">Two elements of values the set keeps apart are one cell.</exception>
        Relations.SetContainer Collect(Set<T> value, uint[] cells)
        {
            var container = Relations.SetContainer.Collect(rebuild, cells);
            if (rebuild == false && container.Count != value.Count)
                throw new ArgumentException(
                    $"Two elements of the set {value} are distinct in their order but one value to {element.Name}, which stores them in one cell: the type's comparer must agree with its equality.",
                    nameof(value));

            return container;
        }

        /// <summary>
        /// Decodes a set's cell, read through the scope's container view. Takes one lock of a container environment, and
        /// more for nested containers.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="scope">The scope serving the call.</param>
        /// <returns>The set.</returns>
        /// <exception cref="InvalidOperationException">No set has the cell.</exception>
        internal override Set<T> Decode(uint cell, Scope? scope)
        {
            var set = Read(cell, Scope.ViewOf(scope));
            var items = new T[set.Count];
            for (var i = 0; i < items.Length; i++)
                items[i] = element.Decode(set.Elements[i], scope);

            return Set<T>.Create(items);
        }

        /// <summary>
        /// Formats a set as egglog reconstructs one, <c>(set-of a b)</c> (set.rs:135-143), its elements in element order.
        /// </summary>
        /// <param name="value">The set.</param>
        /// <returns>The literal.</returns>
        internal override string FormatValue(Set<T> value)
        {
            var parts = new List<string>(value.Count);
            foreach (var item in value)
                parts.Add(element.FormatValue(item));

            return parts.Count == 0 ? "(set-of)" : "(set-of " + string.Join(" ", parts) + ")";
        }

        /// <inheritdoc />
        internal override string DebugValue(Set<T> value) => FormatValue(value);

        /// <summary>
        /// Formats a set's cell as egglog prints it, its elements in cell order, the order its extracted term lists; a cell
        /// no set has in <paramref name="view"/> as <c>#17</c>.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <returns>The literal.</returns>
        internal override string FormatCell(uint cell, Relations.ContainerView view)
        {
            if (view.TryGetContainer(env, cell, out var set) == false)
                return FormatUnresolvedCell(cell);

            var parts = new string[set.Count];
            for (var i = 0; i < parts.Length; i++)
                parts[i] = element.FormatCell(set.Elements[i], view);

            return parts.Length == 0 ? "(set-of)" : "(set-of " + string.Join(" ", parts) + ")";
        }

        /// <summary>
        /// Compares two sets in element order, as <see cref="Set{T}.CompareTo"/> does: element by element, a shorter
        /// prefix first. An order given to <see cref="ValueStore.Register{T}"/> is not the element order, so it orders
        /// neither the elements nor the sets. Never interns.
        /// </summary>
        /// <param name="x">One set.</param>
        /// <param name="y">The other set.</param>
        /// <returns>The order.</returns>
        internal override int CompareValues(Set<T> x, Set<T> y) => x.CompareTo(y);

        /// <summary>
        /// Compares two sets' cells as <see cref="CompareValues"/> compares the sets: a set of values by its values,
        /// decoded; a set of classes through its elements' cells, which order as its classes do, so that it compares
        /// without a scope.
        /// </summary>
        /// <param name="x">One cell.</param>
        /// <param name="y">The other cell.</param>
        /// <param name="view">The containers of the scope the cells belong to.</param>
        /// <returns>The order.</returns>
        internal override int CompareCells(uint x, uint y, Relations.ContainerView view)
        {
            if (x == y)
                return 0;

            if (rebuild == false)
                return CompareValues(Decode(x, null), Decode(y, null));

            var left = Ordered(x, view);
            var right = Ordered(y, view);
            for (var i = 0; i < left.Length && i < right.Length; i++)
            {
                var order = element.CompareCells(left[i], right[i], view);
                if (order != 0)
                    return order;
            }

            return left.Length.CompareTo(right.Length);
        }

        /// <summary>
        /// Gets a set's elements' cells in element order, the content order of <see cref="CompareValues"/>.
        /// </summary>
        uint[] Ordered(uint cell, Relations.ContainerView view)
        {
            var cells = Read(cell, view).Elements.ToArray();
            Array.Sort(cells, view.IsRoot ? rootCellOrder ??= CellOrder(element, view) : CellOrder(element, view));
            return cells;
        }

        /// <summary>
        /// Makes the order of <paramref name="element"/>'s cells through <paramref name="view"/>, in a method of its own so
        /// that only a call that makes one allocates its closure.
        /// </summary>
        static Comparison<uint> CellOrder(ColumnType<T> element, Relations.ContainerView view) =>
            (a, b) => element.CompareCells(a, b, view);

        /// <inheritdoc />
        internal override bool ValuesEqual(Set<T> x, Set<T> y)
        {
            if (x.Count != y.Count)
                return false;

            for (var i = 0; i < x.Count; i++)
                if (element.ValuesEqual(x[i], y[i]) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        /// <remarks>In cell order, as egglog's <c>inner_values</c>.</remarks>
        public void AppendElements(uint cell, Relations.ContainerView view, List<(ColumnType Type, uint Cell)> destination)
        {
            foreach (var item in Read(cell, view).Elements)
                destination.Add((element, item));
        }

        /// <inheritdoc />
        public void AppendLiteralParts(object value, List<(ColumnType Type, object Value)> destination)
        {
            foreach (var item in (Set<T>)value)
                destination.Add((element, item));
        }

        /// <inheritdoc />
        public void AppendDistinctElements(uint cell, Relations.ContainerView view, List<(ColumnType Type, uint Cell)> destination) =>
            AppendElements(cell, view, destination);

        /// <inheritdoc />
        public bool RebuildsInOrder => element is IContainerColumnType { RebuildsInOrder: true };

        /// <inheritdoc />
        public long[] Content(uint cell, Relations.ContainerView view)
        {
            var elements = Read(cell, view).Elements;
            var content = new long[elements.Length];
            for (var i = 0; i < content.Length; i++)
                content[i] = elements[i];

            return content;
        }

        /// <inheritdoc />
        public long[] Rebuild(long[] content, Func<uint, uint> find)
        {
            if (rebuild == false)
                return content;

            var cells = new uint[content.Length];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = find((uint)content[i]);

            var collected = Relations.SetContainer.Collect(rebuild, cells).Elements;
            var rebuilt = new long[collected.Length];
            for (var i = 0; i < rebuilt.Length; i++)
                rebuilt[i] = collected[i];

            return rebuilt;
        }

        /// <inheritdoc />
        public bool SharesLayout(uint cell, Relations.ContainerView view) =>
            view.TryGetContainer(env, cell, out var set) && set.Rebuild == rebuild;

        /// <inheritdoc />
        public int RebuildFlags => rebuild ? 1 : 0;

        /// <inheritdoc />
        public string CanonicalKey(uint cell, Relations.ContainerView view, Func<ColumnType, uint, string> part)
        {
            var parts = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in Read(cell, view).Elements)
                parts.Add(part(element, item));

            return "{" + string.Join(" ", parts) + "}";
        }

        /// <inheritdoc />
        /// <remarks><c>set-of</c>, as egglog's <c>serialized_name</c> (set.rs:145-147).</remarks>
        public string SerializedName(uint cell, Relations.ContainerView view) => "set-of";

        /// <summary>
        /// Gets the number of elements of a set's cell, without decoding the set: <c>set-length</c> over cells.
        /// </summary>
        /// <param name="cell">The set's cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <returns>The number of elements.</returns>
        internal int CountOfCell(uint cell, Relations.ContainerView view) => Read(cell, view).Count;

        /// <summary>
        /// Gets whether a set's cell holds an element, by the element's cell, without decoding the set:
        /// <c>set-contains</c> over cells.
        /// </summary>
        /// <param name="cell">The set's cell.</param>
        /// <param name="elementCell">The element's cell.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <returns>Whether the set holds it.</returns>
        internal bool ContainsCell(uint cell, uint elementCell, Relations.ContainerView view) => Read(cell, view).Elements.BinarySearch(elementCell) >= 0;

        /// <summary>
        /// Gets the element at an index of a set's cell in cell order: egglog's <c>set-get</c>, the <c>BTreeSet</c>'s
        /// <c>nth</c> (set.rs:122).
        /// </summary>
        /// <param name="cell">The set's cell.</param>
        /// <param name="index">The index.</param>
        /// <param name="view">The containers of the scope the cell belongs to.</param>
        /// <param name="item">The element's cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the index is in range.</returns>
        internal bool TryGetAt(uint cell, long index, Relations.ContainerView view, out uint item)
        {
            var elements = Read(cell, view).Elements;
            var found = index >= 0 && index < elements.Length;
            item = found ? elements[(int)index] : 0;
            return found;
        }

        /// <summary>
        /// Gets the element at an index of a set that may not be interned, as <see cref="TryGetAt"/> does, never
        /// interning: an element no write interned has no cell and comes after every one that has, as egglog's
        /// <c>check</c> would intern it after them, so where two such elements decide which is at the index, which one
        /// egglog finds depends on the order it interns them, and there is no result.
        /// </summary>
        /// <param name="value">The set.</param>
        /// <param name="index">The index.</param>
        /// <param name="view">The containers of the scope read, in which an element that is a container of classes is
        /// looked up.</param>
        /// <param name="item">The element, when the result is <c>true</c>.</param>
        /// <returns>Whether the element is determined.</returns>
        internal bool TryGetAtValue(Set<T> value, long index, Relations.ContainerView view, out T item)
        {
            var interned = new List<(uint Cell, T Item)>();
            var fresh = new List<T>();
            foreach (var each in value)
            {
                if (element.TryEncodeExisting(each, view, out var cell))
                    interned.Add((cell, each));
                else
                    fresh.Add(each);
            }

            interned.Sort(static (a, b) => a.Cell.CompareTo(b.Cell));
            item = default!;
            if (index < 0 || index >= value.Count)
                return false;

            if (index < interned.Count)
            {
                item = interned[(int)index].Item;
                return true;
            }

            if (fresh.Count != 1)
                return false;

            item = fresh[0];
            return true;
        }

        /// <summary>
        /// Reads the container of a cell through <paramref name="view"/>, as every read of a set by its cell does, so that
        /// a set of classes a child holds itself is read where the child holds it.
        /// </summary>
        /// <exception cref="InvalidOperationException">No set has the cell.</exception>
        Relations.SetContainer Read(uint cell, Relations.ContainerView view)
        {
            if (view.TryGetContainer(env, cell, out var set) == false)
                throw new InvalidOperationException($"No {Name} has cell {cell}.");

            return set;
        }

    }

}
