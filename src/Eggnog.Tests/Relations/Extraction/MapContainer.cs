using System;
using System.Buffers;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A map of values to values, kept sorted by key: the frontend's <c>MapContainer</c> (src/sort/map.rs), for the
    /// extraction tests. Only the values are ids here (the tests key maps by base values), so rebuilding canonicalizes
    /// the values in place.
    /// </summary>
    readonly struct MapContainer : IContainerValue<MapContainer>
    {

        readonly uint[]? keys;
        readonly uint[]? values;

        /// <summary>
        /// Initializes a map from (key, value) pairs; a repeated key keeps its last value.
        /// </summary>
        public MapContainer(bool rebuildValues, params (uint Key, uint Value)[] entries)
        {
            var sorted = entries.GroupBy(e => e.Key).Select(g => g.Last()).OrderBy(e => e.Key).ToArray();
            keys = sorted.Select(e => e.Key).ToArray();
            values = sorted.Select(e => e.Value).ToArray();
            RebuildValues = rebuildValues;
        }

        MapContainer(bool rebuildValues, uint[] keys, uint[] values)
        {
            this.keys = keys;
            this.values = values;
            RebuildValues = rebuildValues;
        }

        public bool RebuildValues { get; }

        public ReadOnlySpan<uint> Keys => keys;

        public ReadOnlySpan<uint> Values => values;

        public ulong ComputeHash()
        {
            var hasher = new FxHasher();
            hasher.Write(RebuildValues ? 1UL : 0UL);
            hasher.Write((ulong)Keys.Length);
            for (var i = 0; i < Keys.Length; i++)
            {
                hasher.Write(Keys[i]);
                hasher.Write(Values[i]);
            }

            return hasher.Finish();
        }

        public bool TryRebuild(Rebuilder rebuilder, out MapContainer rebuilt)
        {
            if (RebuildValues == false || Values.Length == 0)
            {
                rebuilt = default;
                return false;
            }

            var scratch = ArrayPool<uint>.Shared.Rent(Values.Length);
            var span = scratch.AsSpan(0, Values.Length);
            Values.CopyTo(span);
            var changed = rebuilder.RebuildSlice(span);
            rebuilt = changed ? new MapContainer(true, Keys.ToArray(), span.ToArray()) : default;
            ArrayPool<uint>.Shared.Return(scratch);
            return changed;
        }

        public void ForEachValue<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IContainerElementVisitor
        {
            foreach (var key in Keys)
                visitor.Visit(key);

            foreach (var value in Values)
                visitor.Visit(value);
        }

        public bool Equals(MapContainer other) => RebuildValues == other.RebuildValues && Keys.SequenceEqual(other.Keys) && Values.SequenceEqual(other.Values);

        public override bool Equals(object? obj) => obj is MapContainer other && Equals(other);

        public override int GetHashCode() => (int)ComputeHash();

    }

}
