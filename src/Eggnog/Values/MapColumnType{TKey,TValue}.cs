using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The column type of <see cref="Map{TKey, TValue}"/>: egglog's <c>Map</c> sort (src/sort/map.rs). A map's cell is the
    /// id of its <see cref="Relations.MapContainer"/>, whose entries are in key-cell order as egglog's <c>BTreeMap</c>,
    /// interned in the graph's container environment with an id from the graph's id counter; the engine column is an id
    /// column. A map of values is never rebuilt and its id never merges, so equal maps have equal cells in every scope,
    /// and the library treats it as a value. A map whose keys or values are classes, or hold them, is egglog's eq-sort
    /// container (<see cref="ColumnType.HoldsClasses"/>): rebuilt as egglog's, keys that become equal collapsing, the
    /// later in key-cell order kept, and interned only at the root (decisions row 9).
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    internal sealed class MapColumnType<TKey, TValue> : ColumnType<Map<TKey, TValue>>, IContainerColumnType
        where TKey : notnull
        where TValue : notnull
    {

        readonly ColumnType<TKey> key;
        readonly ColumnType<TValue> value;
        readonly bool rebuildKeys;
        readonly bool rebuildValues;
        readonly Relations.ContainerEnv<Relations.MapContainer> env;
        readonly Relations.CounterSet counters;
        readonly Relations.MapSort sort;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The type's name.</param>
        /// <param name="key">The column type of the keys.</param>
        /// <param name="value">The column type of the values.</param>
        /// <param name="env">The graph's map environment, registered as holding e-class ids when the keys or the values
        /// are classes or hold them.</param>
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
            rebuildKeys = key.InvolvesClasses;
            rebuildValues = value.InvolvesClasses;
            this.env = env;
            this.counters = counters;
            sort = new Relations.MapSort(name, key.ExtractionSort, value.ExtractionSort);
        }

        /// <inheritdoc />
        internal override bool HoldsClasses => rebuildKeys || rebuildValues;

        /// <inheritdoc />
        internal override Relations.ColumnTy EngineType => Relations.ColumnTy.Id;

        /// <inheritdoc />
        public IReadOnlyList<ColumnType> ElementTypes => [key, value];

        /// <inheritdoc />
        internal override bool IsOrdered => key.IsOrdered && value.IsOrdered;

        /// <inheritdoc />
        internal override Relations.Sort ExtractionSort => sort;

        /// <summary>
        /// Encodes a map: its keys and values, then the container, its entries in key-cell order, interned without an
        /// execution state from the graph's id counter. Thread-safe, from any scope.
        /// </summary>
        /// <param name="map">The map.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="ArgumentNullException">The map is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">The map holds classes, which encode in a scope
        /// (<see cref="EncodeIn"/>).</exception>
        /// <exception cref="OverflowException">The graph has no ids left.</exception>
        internal override uint Encode(Map<TKey, TValue> map)
        {
            ArgumentNullException.ThrowIfNull(map);
            if (HoldsClasses)
                throw ContainerClasses.NeedScope(Name);

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

        /// <summary>
        /// Encodes a map a primitive returned in <paramref name="scope"/>: for a map that holds classes, its classes
        /// usable there and the container interned at the root, or in a child the one the root holds; else as
        /// <see cref="Encode"/>.
        /// </summary>
        /// <param name="map">The map.</param>
        /// <param name="scope">The scope the call runs in.</param>
        /// <returns>The cell.</returns>
        /// <exception cref="InvalidOperationException">The map holds classes, and a class is not usable in the scope, or
        /// the scope cannot intern and the root holds no such map.</exception>
        internal override uint EncodeIn(Map<TKey, TValue> map, Scope? scope)
        {
            if (HoldsClasses == false)
                return Encode(map);

            ArgumentNullException.ThrowIfNull(map);
            var keys = new uint[map.Count];
            var values = new uint[map.Count];
            var i = 0;
            foreach (var (k, v) in map)
            {
                keys[i] = key.EncodeIn(k, scope);
                values[i] = value.EncodeIn(v, scope);
                i++;
            }

            return ContainerClasses.Intern(env, Collect(map, keys, values), counters, Name, scope);
        }

        /// <inheritdoc />
        /// <remarks>Class keys that are one class now collapse, the later in key-cell order kept, as egglog's rebuild of a
        /// <c>BTreeMap</c> keeps it; keys that hold classes and are one now are refused (<see cref="Collapsed"/>).</remarks>
        internal override uint EncodeCanonical(Map<TKey, TValue> map, Scope scope, Func<uint, uint> find, ScopeOrigin? openBatch)
        {
            if (HoldsClasses == false)
                return Encode(map);

            ArgumentNullException.ThrowIfNull(map);
            var keys = new uint[map.Count];
            var values = new uint[map.Count];
            var i = 0;
            foreach (var (k, v) in map)
            {
                keys[i] = key.EncodeCanonical(k, scope, find, openBatch);
                values[i] = value.EncodeCanonical(v, scope, find, openBatch);
                i++;
            }

            return find(ContainerClasses.Intern(env, Collect(map, keys, values), counters, Name, scope));
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

            return env.TryGetId(Collect(map, keys, values), out cell);
        }

        /// <inheritdoc />
        internal override bool TryEncodeCanonical(Map<TKey, TValue> map, Scope scope, Func<uint, uint> find, out uint cell)
        {
            if (HoldsClasses == false)
                return TryEncodeExisting(map, out cell);

            ArgumentNullException.ThrowIfNull(map);
            cell = 0;
            var keys = new uint[map.Count];
            var values = new uint[map.Count];
            var i = 0;
            foreach (var (k, v) in map)
            {
                if (key.TryEncodeCanonical(k, scope, find, out keys[i]) == false
                    || value.TryEncodeCanonical(v, scope, find, out values[i]) == false)
                    return false;

                i++;
            }

            if (env.TryGetId(Collect(map, keys, values), out var id) == false)
                return false;

            cell = find(id);
            return true;
        }

        /// <inheritdoc />
        internal override Map<TKey, TValue> Canonical(Map<TKey, TValue> map, Scope scope, Func<uint, uint> find)
        {
            if (HoldsClasses == false)
                return map;

            ArgumentNullException.ThrowIfNull(map);
            var entries = new List<KeyValuePair<TKey, TValue>>(map.Count);
            foreach (var (k, v) in map)
                entries.Add(new(key.Canonical(k, scope, find), value.Canonical(v, scope, find)));

            var canonical = Map<TKey, TValue>.Create(entries);
            if (key.HoldsClasses && canonical.Count != map.Count)
                throw Collapsed(map);

            return canonical;
        }

        /// <summary>
        /// Makes the error for a map whose keys hold classes and are one now: egglog's rebuild keeps the later of them in
        /// the order of their containers' cells, which a map a host holds no longer knows once they are rebuilt, so which
        /// to keep is not known.
        /// </summary>
        ArgumentException Collapsed(Map<TKey, TValue> map) =>
            new($"Two keys of the map {map} hold classes that are one now, so they are one key; which value egglog's rebuild keeps depends on their containers' cells, which this map no longer holds. Make the map anew from the classes as they are.", nameof(map));

        /// <summary>
        /// Makes the container of a map's cells, its entries in key-cell order: class keys may collapse, as egglog's
        /// rebuild makes them; keys of values the map keeps apart but the key type stores as one are refused, and so are
        /// keys that hold classes and are one now (<see cref="Collapsed"/>).
        /// </summary>
        /// <exception cref="ArgumentException">Two keys of values the map keeps apart are one cell, or two keys that hold
        /// classes are.</exception>
        Relations.MapContainer Collect(Map<TKey, TValue> map, uint[] keys, uint[] values)
        {
            var container = Relations.MapContainer.Collect(rebuildKeys, rebuildValues, keys, values);
            if (container.Count != map.Count && rebuildKeys == false)
                throw Merged(map);

            if (container.Count != map.Count && key.HoldsClasses)
                throw Collapsed(map);

            return container;
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
        /// Compares two maps' cells as <see cref="CompareValues"/> compares the maps: a map of values by its entries,
        /// decoded; a map of classes through its keys' and values' cells, which order as its classes do, so that it
        /// compares without a scope.
        /// </summary>
        /// <param name="x">One cell.</param>
        /// <param name="y">The other cell.</param>
        /// <returns>The order.</returns>
        internal override int CompareCells(uint x, uint y)
        {
            if (x == y)
                return 0;

            if (rebuildKeys == false && rebuildValues == false)
                return CompareValues(Decode(x, null), Decode(y, null));

            var left = Entries(x);
            var right = Entries(y);
            for (var i = 0; i < left.Length && i < right.Length; i++)
            {
                var order = key.CompareCells(left[i].Key, right[i].Key);
                if (order == 0)
                    order = value.CompareCells(left[i].Value, right[i].Value);

                if (order != 0)
                    return order;
            }

            return left.Length.CompareTo(right.Length);
        }

        /// <summary>
        /// Gets a map's entries' cells in key order, the content order of <see cref="CompareValues"/>.
        /// </summary>
        (uint Key, uint Value)[] Entries(uint cell)
        {
            var map = Read(cell);
            var entries = new (uint Key, uint Value)[map.Count];
            for (var i = 0; i < entries.Length; i++)
                entries[i] = (map.Keys[i], map.Values[i]);

            Array.Sort(entries, (a, b) => key.CompareCells(a.Key, b.Key));
            return entries;
        }

        /// <inheritdoc />
        public void AppendDistinctElements(uint cell, List<(ColumnType Type, uint Cell)> destination) => AppendElements(cell, destination);

        /// <inheritdoc />
        public void AppendLiteralParts(object value, List<(ColumnType Type, object Value)> destination)
        {
            foreach (var (entryKey, entryValue) in (Map<TKey, TValue>)value)
            {
                destination.Add((key, entryKey));
                destination.Add((this.value, entryValue));
            }
        }

        /// <inheritdoc />
        public bool RebuildsInOrder =>
            rebuildKeys || key is IContainerColumnType { RebuildsInOrder: true } || value is IContainerColumnType { RebuildsInOrder: true };

        /// <inheritdoc />
        public long[] Content(uint cell)
        {
            var map = Read(cell);
            var content = new long[map.Count * 2];
            for (var i = 0; i < map.Count; i++)
            {
                content[2 * i] = map.Keys[i];
                content[2 * i + 1] = map.Values[i];
            }

            return content;
        }

        /// <inheritdoc />
        /// <remarks>As <see cref="Relations.MapContainer"/>'s rebuild, egglog's: the keys and values rebuilt, then the
        /// entries collected again in their order, the later of two equal keys kept.</remarks>
        public long[] Rebuild(long[] content, Func<uint, uint> find)
        {
            if (HoldsClasses == false)
                return content;

            var keys = new uint[content.Length / 2];
            var values = new uint[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = rebuildKeys ? find((uint)content[2 * i]) : (uint)content[2 * i];
                values[i] = rebuildValues ? find((uint)content[2 * i + 1]) : (uint)content[2 * i + 1];
            }

            var collected = Relations.MapContainer.Collect(rebuildKeys, rebuildValues, keys, values);
            var rebuilt = new long[collected.Count * 2];
            for (var i = 0; i < collected.Count; i++)
            {
                rebuilt[2 * i] = collected.Keys[i];
                rebuilt[2 * i + 1] = collected.Values[i];
            }

            return rebuilt;
        }

        /// <inheritdoc />
        public string CanonicalKey(uint cell, Func<ColumnType, uint, string> part)
        {
            var map = Read(cell);
            var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < map.Count; i++)
                entries[part(key, map.Keys[i])] = part(value, map.Values[i]);

            var text = new System.Text.StringBuilder("{");
            foreach (var (k, v) in entries)
                text.Append(k).Append(':').Append(v).Append(' ');

            return text.Append('}').ToString();
        }

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
        /// Compares two maps in element order, as <see cref="Map{TKey, TValue}.CompareTo"/> does: their entries in key
        /// order, each by key then value, a shorter prefix first. An order given to <see cref="ValueStore.Register{T}"/>
        /// is not the element order, so it orders neither the keys nor the maps. Never interns.
        /// </summary>
        /// <param name="x">One map.</param>
        /// <param name="y">The other map.</param>
        /// <returns>The order.</returns>
        internal override int CompareValues(Map<TKey, TValue> x, Map<TKey, TValue> y) => x.CompareTo(y);

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
