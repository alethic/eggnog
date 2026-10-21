using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The column type of <see cref="Map{TKey, TValue}"/>: egglog's <c>Map</c> sort (src/sort/map.rs) over value key and
    /// value types. A map's cell is the id of its <see cref="Relations.MapContainer"/>, whose entries are in key-cell
    /// order as egglog's <c>BTreeMap</c>, interned in the graph's container environment with an id from the graph's id
    /// counter; the engine column is an id column. A map of values is never rebuilt and its id never merges, so equal maps
    /// have equal cells in every scope, and the library treats it as a value.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    internal sealed class MapColumnType<TKey, TValue> : ColumnType<Map<TKey, TValue>>, IContainerColumnType
        where TKey : notnull
        where TValue : notnull
    {

        readonly ColumnType<TKey> key;
        readonly ColumnType<TValue> value;
        readonly Relations.ContainerEnv<Relations.MapContainer> env;
        readonly Relations.CounterSet counters;
        readonly Relations.MapSort sort;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The type's name.</param>
        /// <param name="key">The column type of the keys; a value type.</param>
        /// <param name="value">The column type of the values; a value type.</param>
        /// <param name="env">The graph's map environment, registered as holding no e-class ids.</param>
        /// <param name="counters">The graph's counters, from which the environment mints ids.</param>
        public MapColumnType(
            string name,
            ColumnType<TKey> key,
            ColumnType<TValue> value,
            Relations.ContainerEnv<Relations.MapContainer> env,
            Relations.CounterSet counters)
            : base(name, ColumnKind.Value)
        {
            this.key = key;
            this.value = value;
            this.env = env;
            this.counters = counters;
            sort = new Relations.MapSort(name, key.ExtractionSort, value.ExtractionSort);
        }

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => Relations.ColumnTy.Id;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <summary>
        /// Encodes a map: its keys and values, then the container, its entries in key-cell order, interned without an
        /// execution state from the graph's id counter. Thread-safe, from any scope.
        /// </summary>
        /// <param name="map">The map.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="ArgumentNullException">The map is <c>null</c>.</exception>
        /// <exception cref="OverflowException">The graph has no ids left.</exception>
        internal override uint Encode(Map<TKey, TValue> map)
        {
            ArgumentNullException.ThrowIfNull(map);
            var keys = new uint[map.Count];
            var values = new uint[map.Count];
            var i = 0;
            foreach (var (k, v) in map)
            {
                keys[i] = key.Encode(k);
                values[i] = value.Encode(v);
                i++;
            }

            var container = Relations.MapContainer.Collect(false, false, keys, values);
            if (container.Count != map.Count)
                throw Merged(map);

            return env.GetOrInsertShared(container, counters);
        }

        /// <inheritdoc />
        internal override bool TryEncodeExisting(Map<TKey, TValue> map, out uint cell)
        {
            ArgumentNullException.ThrowIfNull(map);
            cell = 0;
            var keys = new uint[map.Count];
            var values = new uint[map.Count];
            var i = 0;
            foreach (var (k, v) in map)
            {
                if (key.TryEncodeExisting(k, out keys[i]) == false || value.TryEncodeExisting(v, out values[i]) == false)
                    return false;

                i++;
            }

            var container = Relations.MapContainer.Collect(false, false, keys, values);
            if (container.Count != map.Count)
                throw Merged(map);

            return env.TryGetId(container, out cell);
        }

        /// <summary>
        /// Decodes a map's cell. Takes one lock of the container environment, and more for nested containers.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <param name="scope">The scope serving the call.</param>
        /// <returns>The map.</returns>
        /// <exception cref="InvalidOperationException">No map has the cell.</exception>
        internal override Map<TKey, TValue> Decode(uint cell, Scope? scope)
        {
            var map = Read(cell);
            var keys = new TKey[map.Count];
            var values = new TValue[map.Count];
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = key.Decode(map.Keys[i], scope);
                values[i] = value.Decode(map.Values[i], scope);
            }

            return Map<TKey, TValue>.FromDistinct(keys, values);
        }

        /// <summary>
        /// Formats a map as egglog reconstructs one, a chain of <c>map-insert</c>s over <c>(map-empty)</c>
        /// (map.rs:157-171), its entries in key order.
        /// </summary>
        /// <param name="map">The map.</param>
        /// <returns>The literal.</returns>
        internal override string FormatValue(Map<TKey, TValue> map)
        {
            var text = "(map-empty)";
            foreach (var (k, v) in map)
                text = "(map-insert " + text + " " + key.FormatValue(k) + " " + value.FormatValue(v) + ")";

            return text;
        }

        /// <inheritdoc />
        internal override string DebugValue(Map<TKey, TValue> map) => FormatValue(map);

        /// <summary>
        /// Formats a map's cell as egglog prints it, its entries in key-cell order, the order its extracted term lists.
        /// </summary>
        /// <param name="cell">The cell.</param>
        /// <returns>The literal.</returns>
        internal override string FormatCell(uint cell)
        {
            var map = Read(cell);
            var text = "(map-empty)";
            for (var i = 0; i < map.Count; i++)
                text = "(map-insert " + text + " " + key.FormatCell(map.Keys[i]) + " " + value.FormatCell(map.Values[i]) + ")";

            return text;
        }

        /// <summary>
        /// Compares two maps' cells as <see cref="CompareValues"/> compares the maps, decoding each once.
        /// </summary>
        /// <param name="x">One cell.</param>
        /// <param name="y">The other cell.</param>
        /// <returns>The order.</returns>
        internal override int CompareCells(uint x, uint y) => x == y ? 0 : CompareValues(Decode(x, null), Decode(y, null));

        /// <summary>
        /// Gets a key's value from a map's cell, by the key's cell, without decoding the map: <c>map-get</c> over cells.
        /// </summary>
        /// <param name="cell">The map's cell.</param>
        /// <param name="keyCell">The key's cell.</param>
        /// <param name="valueCell">The value's cell, when the result is <c>true</c>.</param>
        /// <returns>Whether the map has the key.</returns>
        internal bool TryGetCell(uint cell, uint keyCell, out uint valueCell)
        {
            var map = Read(cell);
            var at = map.Keys.BinarySearch(keyCell);
            valueCell = at >= 0 ? map.Values[at] : 0;
            return at >= 0;
        }

        /// <summary>
        /// Makes the error for a map whose order keeps two keys apart that the key type's codec stores as one.
        /// </summary>
        ArgumentException Merged(Map<TKey, TValue> map) =>
            new($"Two keys of the map {map} are distinct in their order but one value to {key.Name}, which stores them in one cell: the type's comparer must agree with its equality.", nameof(map));

        /// <summary>
        /// Compares two maps lexicographically over their entries in key order, each by key then value, a shorter prefix
        /// first. Never interns.
        /// </summary>
        /// <param name="x">One map.</param>
        /// <param name="y">The other map.</param>
        /// <returns>The order.</returns>
        internal override int CompareValues(Map<TKey, TValue> x, Map<TKey, TValue> y)
        {
            using var left = x.GetEnumerator();
            using var right = y.GetEnumerator();
            while (true)
            {
                var hasLeft = left.MoveNext();
                var hasRight = right.MoveNext();
                if (hasLeft == false || hasRight == false)
                    return hasLeft.CompareTo(hasRight);

                var order = key.CompareValues(left.Current.Key, right.Current.Key);
                if (order == 0)
                    order = value.CompareValues(left.Current.Value, right.Current.Value);

                if (order != 0)
                    return order;
            }
        }

        /// <inheritdoc />
        internal override bool ValuesEqual(Map<TKey, TValue> x, Map<TKey, TValue> y)
        {
            if (x.Count != y.Count)
                return false;

            using var left = x.GetEnumerator();
            using var right = y.GetEnumerator();
            while (left.MoveNext() && right.MoveNext())
            {
                if (key.ValuesEqual(left.Current.Key, right.Current.Key) == false)
                    return false;

                if (value.ValuesEqual(left.Current.Value, right.Current.Value) == false)
                    return false;
            }

            return true;
        }

        /// <inheritdoc />
        /// <remarks>Keys and values alternating, in key-cell order, as egglog's <c>inner_values</c> (map.rs:124-137).</remarks>
        public void AppendElements(uint cell, List<(ColumnType Type, uint Cell)> destination)
        {
            var map = Read(cell);
            for (var i = 0; i < map.Count; i++)
            {
                destination.Add((key, map.Keys[i]));
                destination.Add((value, map.Values[i]));
            }
        }

        /// <inheritdoc />
        /// <remarks>The map sort's name, as egglog's <c>serialized_name</c> (map.rs:173-175).</remarks>
        public string SerializedName(uint cell) => Name;

        /// <summary>
        /// Reads the container of a cell.
        /// </summary>
        /// <exception cref="InvalidOperationException">No map has the cell.</exception>
        Relations.MapContainer Read(uint cell)
        {
            if (env.TryGetContainer(cell, out var map) == false)
                throw new InvalidOperationException($"No {Name} has cell {cell}.");

            return map;
        }

    }

}
