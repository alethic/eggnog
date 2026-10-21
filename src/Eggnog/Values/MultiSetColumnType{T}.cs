using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The column type of <see cref="MultiSet{T}"/>: egglog's <c>MultiSet</c> sort (src/sort/multiset.rs) over a value
    /// element type. A multiset's cell is the id of its <see cref="Relations.MultiSetContainer"/>, whose elements are in
    /// cell order as egglog's <c>BTreeMap</c>, interned in the graph's container environment with an id from the graph's
    /// id counter; the engine column is an id column. A multiset of values is never rebuilt and its id never merges, so
    /// equal multisets have equal cells in every scope, and the library treats it as a value.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    internal sealed class MultiSetColumnType<T> : ColumnType<MultiSet<T>>, IContainerColumnType where T : notnull
    {

        readonly ColumnType<T> element;
        readonly Relations.ContainerEnv<Relations.MultiSetContainer> env;
        readonly Relations.CounterSet counters;
        readonly Relations.MultiSetSort sort;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The type's name.</param>
        /// <param name="element">The column type of the elements; a value type.</param>
        /// <param name="env">The graph's multiset environment, registered as holding no e-class ids.</param>
        /// <param name="counters">The graph's counters, from which the environment mints ids.</param>
        public MultiSetColumnType(
            string name,
            ColumnType<T> element,
            Relations.ContainerEnv<Relations.MultiSetContainer> env,
            Relations.CounterSet counters)
            : base(name, ColumnKind.Value)
        {
            this.element = element;
            this.env = env;
            this.counters = counters;
            sort = new Relations.MultiSetSort(name, element.ExtractionSort);
        }

        /// <summary>
        /// Gets the column type of the elements.
        /// </summary>
        internal ColumnType<T> Element => element;

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => Relations.ColumnTy.Id;

        /// <inheritdoc />
        public IReadOnlyList<ColumnType> ElementTypes => [element];

        /// <inheritdoc />
        internal override bool IsOrdered => element.IsOrdered;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <summary>
        /// Encodes a multiset: its distinct elements, then the container, its elements in cell order, interned without an
        /// execution state from the graph's id counter. Thread-safe, from any scope.
        /// </summary>
        /// <param name="value">The multiset.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="ArgumentNullException">The multiset is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Two elements the multiset keeps apart are one value to the element
        /// type.</exception>
        /// <exception cref="OverflowException">The graph has no ids left.</exception>
        internal override uint Encode(MultiSet<T> value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var (items, counts) = Split(value);
            var cells = new uint[items.Length];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = element.Encode(items[i]);

            var container = Relations.MultiSetContainer.FromCounts(false, cells, counts);
            if (container.Elements.Length != items.Length)
                throw Merged(value);

            return env.GetOrInsertShared(container, counters);
        }

        /// <inheritdoc />
        internal override bool TryEncodeExisting(MultiSet<T> value, out uint cell)
        {
            ArgumentNullException.ThrowIfNull(value);
            cell = 0;
            var (items, counts) = Split(value);
            var cells = new uint[items.Length];
            for (var i = 0; i < cells.Length; i++)
                if (element.TryEncodeExisting(items[i], out cells[i]) == false)
                    return false;

            var container = Relations.MultiSetContainer.FromCounts(false, cells, counts);
            if (container.Elements.Length != items.Length)
                throw Merged(value);

            return env.TryGetId(container, out cell);
        }

        /// <summary>
        /// Decodes a multiset's cell. Takes one lock of the container environment, and more for nested containers.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="scope">The scope serving the call.</param>
        /// <returns>The multiset.</returns>
        /// <exception cref="InvalidOperationException">No multiset has the cell.</exception>
        internal override MultiSet<T> Decode(uint cell, Scope? scope)
        {
            var multiset = Read(cell);
            var entries = new KeyValuePair<T, long>[multiset.Elements.Length];
            for (var i = 0; i < entries.Length; i++)
                entries[i] = new(element.Decode(multiset.Elements[i], scope), multiset.Counts[i]);

            return MultiSet<T>.FromCounts(entries);
        }

        /// <summary>
        /// Formats a multiset as egglog reconstructs one, <c>(multiset-of a a b)</c> (multiset.rs:211-219), its elements
        /// in element order.
        /// </summary>
        /// <param name="value">The multiset.</param>
        /// <returns>The literal.</returns>
        internal override string FormatValue(MultiSet<T> value)
        {
            var parts = new List<string>();
            foreach (var item in value)
                parts.Add(element.FormatValue(item));

            return parts.Count == 0 ? "(multiset-of)" : "(multiset-of " + string.Join(" ", parts) + ")";
        }

        /// <inheritdoc />
        internal override string DebugValue(MultiSet<T> value) => FormatValue(value);

        /// <summary>
        /// Formats a multiset's cell as egglog prints it, its elements in cell order, the order its extracted term
        /// lists.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <returns>The literal.</returns>
        internal override string FormatCell(uint cell)
        {
            var multiset = Read(cell);
            var parts = new List<string>();
            for (var i = 0; i < multiset.Elements.Length; i++)
            {
                var part = element.FormatCell(multiset.Elements[i]);
                for (var k = 0L; k < multiset.Counts[i]; k++)
                    parts.Add(part);
            }

            return parts.Count == 0 ? "(multiset-of)" : "(multiset-of " + string.Join(" ", parts) + ")";
        }

        /// <summary>
        /// Compares two multisets lexicographically over their elements with their repeats in element order, a shorter
        /// prefix first. Never interns.
        /// </summary>
        /// <param name="x">One multiset.</param>
        /// <param name="y">The other multiset.</param>
        /// <returns>The order.</returns>
        internal override int CompareValues(MultiSet<T> x, MultiSet<T> y)
        {
            using var left = x.GetEnumerator();
            using var right = y.GetEnumerator();
            while (true)
            {
                var hasLeft = left.MoveNext();
                var hasRight = right.MoveNext();
                if (hasLeft == false || hasRight == false)
                    return hasLeft.CompareTo(hasRight);

                var order = element.CompareValues(left.Current, right.Current);
                if (order != 0)
                    return order;
            }
        }

        /// <inheritdoc />
        internal override bool ValuesEqual(MultiSet<T> x, MultiSet<T> y)
        {
            if (x.Count != y.Count)
                return false;

            using var left = x.Counts.GetEnumerator();
            using var right = y.Counts.GetEnumerator();
            while (left.MoveNext())
            {
                if (right.MoveNext() == false || left.Current.Value != right.Current.Value)
                    return false;

                if (element.ValuesEqual(left.Current.Key, right.Current.Key) == false)
                    return false;
            }

            return right.MoveNext() == false;
        }

        /// <inheritdoc />
        /// <remarks>Each element repeated by its count, in cell order, as egglog's <c>inner_values</c>
        /// (multiset.rs:114-126).</remarks>
        public void AppendElements(uint cell, List<(ColumnType Type, uint Cell)> destination)
        {
            var multiset = Read(cell);
            for (var i = 0; i < multiset.Elements.Length; i++)
                for (var k = 0L; k < multiset.Counts[i]; k++)
                    destination.Add((element, multiset.Elements[i]));
        }

        /// <inheritdoc />
        /// <remarks><c>multiset-of</c>, as egglog's <c>serialized_name</c> (multiset.rs:221-223).</remarks>
        public string SerializedName(uint cell) => "multiset-of";

        /// <summary>
        /// Gets how many times an element occurs in a multiset's cell, by the element's cell, without decoding the
        /// multiset: <c>multiset-count</c> over cells.
        /// </summary>
        /// <param name="cell">The multiset's cell.</param>
        /// <param name="elementCell">The element's cell.</param>
        /// <returns>Its count; 0 when it does not occur.</returns>
        internal long CountOfCell(uint cell, uint elementCell)
        {
            var multiset = Read(cell);
            var at = multiset.Elements.BinarySearch(elementCell);
            return at >= 0 ? multiset.Counts[at] : 0;
        }

        /// <summary>
        /// Picks the element of least cell: egglog's <c>multiset-pick</c>, the first key of its <c>BTreeMap</c>
        /// (multiset.rs:874-877).
        /// </summary>
        /// <param name="cell">The multiset's cell.</param>
        /// <param name="picked">The element's cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the multiset has an element.</returns>
        internal bool TryPick(uint cell, out uint picked)
        {
            var multiset = Read(cell);
            picked = multiset.Elements.Length > 0 ? multiset.Elements[0] : 0;
            return multiset.Elements.Length > 0;
        }

        /// <summary>
        /// Picks an element of greatest count, of those the one of greatest cell, as Rust's <c>max_by_key</c> keeps the
        /// last maximum: egglog's <c>multiset-pick-max</c> (multiset.rs:196-198).
        /// </summary>
        /// <param name="cell">The multiset's cell.</param>
        /// <param name="picked">The element's cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the multiset has an element.</returns>
        internal bool TryPickMax(uint cell, out uint picked)
        {
            var multiset = Read(cell);
            picked = 0;
            var best = -1;
            for (var i = 0; i < multiset.Elements.Length; i++)
                if (best < 0 || multiset.Counts[i] >= multiset.Counts[best])
                    best = i;

            if (best < 0)
                return false;

            picked = multiset.Elements[best];
            return true;
        }

        /// <summary>
        /// Picks as <see cref="TryPick"/> does from a multiset that may not be interned, never interning: an element no
        /// write interned has no cell and comes after every one that has, as egglog's <c>check</c> would intern it after
        /// them, so of two such elements, with none interned before them, which egglog picks depends on the order it
        /// interns them, and there is no result.
        /// </summary>
        /// <param name="value">The multiset.</param>
        /// <param name="picked">The element, when the result is <c>true</c>.</param>
        /// <returns>Whether the element is determined.</returns>
        internal bool TryPickValue(MultiSet<T> value, out T picked)
        {
            var (interned, fresh) = Classify(value, static _ => true);
            if (interned.Count > 0)
            {
                picked = interned[0].Item;
                return true;
            }

            return TryOne(fresh, out picked);
        }

        /// <summary>
        /// Picks as <see cref="TryPickMax"/> does from a multiset that may not be interned, never interning (see
        /// <see cref="TryPickValue"/>): of the elements of greatest count, the last in cell order, which is one with no
        /// cell when there is one, and no result when there are two.
        /// </summary>
        /// <param name="value">The multiset.</param>
        /// <param name="picked">The element, when the result is <c>true</c>.</param>
        /// <returns>Whether the element is determined.</returns>
        internal bool TryPickMaxValue(MultiSet<T> value, out T picked)
        {
            var max = 0L;
            foreach (var (_, count) in value.Counts)
                max = Math.Max(max, count);

            var (interned, fresh) = Classify(value, count => count == max);
            if (fresh.Count > 0)
                return TryOne(fresh, out picked);

            if (interned.Count > 0)
            {
                picked = interned[^1].Item;
                return true;
            }

            picked = default!;
            return false;
        }

        /// <summary>
        /// Splits a multiset's distinct elements, of the counts <paramref name="keep"/> accepts, into those with a cell,
        /// in cell order, and those without.
        /// </summary>
        (List<(uint Cell, T Item)> Interned, List<T> Fresh) Classify(MultiSet<T> value, Func<long, bool> keep)
        {
            var interned = new List<(uint Cell, T Item)>();
            var fresh = new List<T>();
            foreach (var (item, count) in value.Counts)
            {
                if (keep(count) == false)
                    continue;

                if (element.TryEncodeExisting(item, out var cell))
                    interned.Add((cell, item));
                else
                    fresh.Add(item);
            }

            interned.Sort(static (a, b) => a.Cell.CompareTo(b.Cell));
            return (interned, fresh);
        }

        /// <summary>
        /// Gets the only element of a list, if it has exactly one.
        /// </summary>
        static bool TryOne(List<T> items, out T item)
        {
            item = items.Count == 1 ? items[0] : default!;
            return items.Count == 1;
        }

        /// <summary>
        /// Gets a multiset's distinct elements and their counts, in element order.
        /// </summary>
        static (T[] Items, long[] Counts) Split(MultiSet<T> value)
        {
            var items = new List<T>();
            var counts = new List<long>();
            foreach (var (item, count) in value.Counts)
            {
                items.Add(item);
                counts.Add(count);
            }

            return (items.ToArray(), counts.ToArray());
        }

        /// <summary>
        /// Makes the error for a multiset whose order keeps two elements apart that the element type's codec stores as
        /// one.
        /// </summary>
        ArgumentException Merged(MultiSet<T> multiset) =>
            new($"Two elements of the multiset {multiset} are distinct in their order but one value to {element.Name}, which stores them in one cell: the type's comparer must agree with its equality.", nameof(multiset));

        /// <summary>
        /// Reads the container of a cell.
        /// </summary>
        /// <exception cref="InvalidOperationException">No multiset has the cell.</exception>
        Relations.MultiSetContainer Read(uint cell)
        {
            if (env.TryGetContainer(cell, out var multiset) == false)
                throw new InvalidOperationException($"No {Name} has cell {cell}.");

            return multiset;
        }

    }

}
