using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A map from values to values: egglog's <c>MapContainer</c> (src/sort/map.rs:4-38), a <c>BTreeMap&lt;Value,
    /// Value&gt;</c> with its two rebuild flags, which are part of its identity as egglog's derived <c>Eq</c> and
    /// <c>Hash</c> make them. The entries are kept in ascending order of the keys' cells, as egglog's <c>BTreeMap</c> keeps
    /// them, and the keys are distinct.
    /// </summary>
    internal readonly struct MapContainer : IContainerValue<MapContainer>
    {

        readonly uint[]? keys;
        readonly uint[]? values;

        /// <summary>
        /// Initializes a new instance over entries in ascending order of their distinct keys, which it copies.
        /// </summary>
        /// <param name="rebuildKeys">Whether the keys are rebuilt.</param>
        /// <param name="rebuildValues">Whether the values are rebuilt.</param>
        /// <param name="keys">The keys' cells, ascending and distinct.</param>
        /// <param name="values">The values' cells, one per key.</param>
        /// <exception cref="ArgumentException">The keys are not ascending and distinct, or the counts differ.</exception>
        public MapContainer(bool rebuildKeys, bool rebuildValues, ReadOnlySpan<uint> keys, ReadOnlySpan<uint> values)
        {
            if (keys.Length != values.Length)
                throw new ArgumentException("A map has one value per key.", nameof(values));

            for (var i = 1; i < keys.Length; i++)
                if (keys[i - 1] >= keys[i])
                    throw new ArgumentException("A map's keys are ascending and distinct.", nameof(keys));

            RebuildKeys = rebuildKeys;
            RebuildValues = rebuildValues;
            this.keys = keys.ToArray();
            this.values = values.ToArray();
        }

        /// <summary>
        /// Gets whether the keys are rebuilt: egglog's <c>do_rebuild_keys</c>.
        /// </summary>
        public bool RebuildKeys { get; }

        /// <summary>
        /// Gets whether the values are rebuilt: egglog's <c>do_rebuild_vals</c>.
        /// </summary>
        public bool RebuildValues { get; }

        /// <summary>
        /// Gets the keys' cells, ascending.
        /// </summary>
        public ReadOnlySpan<uint> Keys => keys;

        /// <summary>
        /// Gets the values' cells, one per key.
        /// </summary>
        public ReadOnlySpan<uint> Values => values;

        /// <summary>
        /// Gets the number of entries.
        /// </summary>
        public int Count => keys?.Length ?? 0;

        /// <summary>
        /// Makes a map of entries in any order, as egglog collects a <c>BTreeMap</c>: sorted by key cell, and of entries
        /// with equal keys, the last one kept.
        /// </summary>
        /// <param name="rebuildKeys">Whether the keys are rebuilt.</param>
        /// <param name="rebuildValues">Whether the values are rebuilt.</param>
        /// <param name="keys">The keys' cells; reordered in place.</param>
        /// <param name="values">The values' cells, one per key; reordered in place.</param>
        /// <returns>The map.</returns>
        public static MapContainer Collect(bool rebuildKeys, bool rebuildValues, uint[] keys, uint[] values)
        {
            // A stable sort keeps equal keys in their order, so that the last one wins, as BTreeMap's insert does.
            var order = new int[keys.Length];
            for (var i = 0; i < order.Length; i++)
                order[i] = i;

            Array.Sort(order, Comparer<int>.Create((a, b) => keys[a] != keys[b] ? keys[a].CompareTo(keys[b]) : a.CompareTo(b)));
            var sortedKeys = new List<uint>(keys.Length);
            var sortedValues = new List<uint>(keys.Length);
            foreach (var at in order)
            {
                if (sortedKeys.Count > 0 && sortedKeys[^1] == keys[at])
                {
                    sortedValues[^1] = values[at];
                    continue;
                }

                sortedKeys.Add(keys[at]);
                sortedValues.Add(values[at]);
            }

            return new MapContainer(rebuildKeys, rebuildValues, sortedKeys.ToArray(), sortedValues.ToArray());
        }

        /// <inheritdoc />
        public bool HoldsIds => RebuildKeys || RebuildValues;

        /// <inheritdoc />
        public ulong ComputeHash()
        {
            var hasher = new FxHasher();
            hasher.Write(RebuildKeys ? 1UL : 0UL);
            hasher.Write(RebuildValues ? 1UL : 0UL);
            hasher.Write((ulong)Count);
            for (var i = 0; i < Count; i++)
            {
                hasher.Write(Keys[i]);
                hasher.Write(Values[i]);
            }

            return hasher.Finish();
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>rebuild_contents</c> (map.rs:12-34): flagged keys are canonicalized and the map collected again,
        /// so keys that became equal collapse, the later of them in the old order keeping its value; flagged values are
        /// canonicalized in place.
        /// </remarks>
        public bool TryRebuild(Rebuilder rebuilder, out MapContainer rebuilt)
        {
            if ((RebuildKeys == false && RebuildValues == false) || Count == 0)
            {
                rebuilt = default;
                return false;
            }

            var newKeys = Keys.ToArray();
            var newValues = Values.ToArray();
            var changed = false;
            if (RebuildKeys)
                changed |= rebuilder.RebuildSlice(newKeys);

            if (RebuildValues)
                changed |= rebuilder.RebuildSlice(newValues);

            if (changed == false)
            {
                rebuilt = default;
                return false;
            }

            rebuilt = Collect(RebuildKeys, RebuildValues, newKeys, newValues);
            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Visits the flagged parts only, where egglog's <c>iter</c> yields every key and value: a part that is never
        /// rebuilt is never affected by a union (see <see cref="PairContainer.ForEachValue"/>).
        /// </remarks>
        public void ForEachValue<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IContainerElementVisitor
        {
            for (var i = 0; i < Count; i++)
            {
                if (RebuildKeys)
                    visitor.Visit(Keys[i]);

                if (RebuildValues)
                    visitor.Visit(Values[i]);
            }
        }

        /// <inheritdoc />
        public bool Equals(MapContainer other) =>
            RebuildKeys == other.RebuildKeys && RebuildValues == other.RebuildValues
                && Keys.SequenceEqual(other.Keys) && Values.SequenceEqual(other.Values);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is MapContainer other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (int)ComputeHash();

        /// <inheritdoc />
        public override string ToString()
        {
            var parts = new string[Count];
            for (var i = 0; i < parts.Length; i++)
                parts[i] = Keys[i] + ": " + Values[i];

            return "map{" + string.Join(", ", parts) + "}";
        }

    }

}
