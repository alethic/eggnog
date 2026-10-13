using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A frozen chain of overlay levels collapsed into one immutable map from root-canonical ids to the chain's
    /// canonical ids: what a frozen context gives its children, so their finds cost one probe above the root however
    /// deep the frozen ancestry is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Domain: the keys of the chain's own prefix plus every member of every level, all canonical in the root. Content:
    /// <c>m → MapAboveRoot(m)</c> wherever that differs from <c>m</c>; outside the domain the chain maps every
    /// root-canonical id to itself, so <see cref="Map"/> is the identity there and the map is complete.
    /// </para>
    /// <para>
    /// Representation: a dense array over <c>[min, max]</c> of the stored keys, identity-filled, when that range is at
    /// most <see cref="DenseFactor"/> times the number of entries (one bounds check and one load per map); otherwise a
    /// hash table (one probe). <see cref="Build"/> returns <c>null</c> when nothing would be stored. Immutable, so any
    /// number of threads may map concurrently.
    /// </para>
    /// </remarks>
    internal sealed class FlattenedUnionFind
    {

        /// <summary>
        /// The largest ratio of the key range to the number of entries for which the dense representation is used.
        /// </summary>
        public const int DenseFactor = 8;

        readonly uint[]? dense;
        readonly uint min;
        readonly RawHashTable<IdEntry>? hash;
        readonly int count;

        FlattenedUnionFind(uint[] dense, uint min, int count)
        {
            this.dense = dense;
            this.min = min;
            this.count = count;
        }

        FlattenedUnionFind(RawHashTable<IdEntry> hash)
        {
            this.hash = hash;
            count = hash.Count;
        }

        /// <summary>
        /// Gets the number of ids the map moves.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets whether the map is a dense array rather than a hash table.
        /// </summary>
        public bool IsDense => dense is not null;

        /// <summary>
        /// Flattens <paramref name="chain"/>: tabulates <see cref="LayeredUnionFind.MapAboveRoot"/> over the chain's
        /// prefix keys and level members, when a child context freezes. Every level must be current with its parent, as
        /// a freezing context's chain is. O(domain).
        /// </summary>
        /// <param name="chain">The chain of a context being frozen.</param>
        /// <returns>The map, or <c>null</c> when the chain moves no id.</returns>
        public static FlattenedUnionFind? Build(LayeredUnionFind chain)
        {
            ArgumentNullException.ThrowIfNull(chain);

            var keys = new List<uint>();
            chain.Prefix?.CollectKeys(keys);
            foreach (var level in chain.Levels)
                for (var slot = 0; slot < level.SlotCount; slot++)
                    foreach (var member in level.MembersOf(slot))
                        keys.Add(member);

            keys.Sort();
            var moved = new List<uint>();
            var targets = new List<uint>();
            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                if (i > 0 && keys[i - 1] == key)
                    continue;

                var target = chain.MapAboveRoot(key);
                if (target != key)
                {
                    moved.Add(key);
                    targets.Add(target);
                }
            }

            return FromPairs(moved.ToArray(), targets.ToArray());
        }

        /// <summary>
        /// Creates the map holding <c>keys[i] → values[i]</c>, choosing the representation by the key range.
        /// </summary>
        /// <param name="keys">The ids moved, distinct.</param>
        /// <param name="values">Their targets, each different from its key.</param>
        /// <returns>The map, or <c>null</c> when there are no pairs.</returns>
        /// <exception cref="ArgumentException">The spans differ in length.</exception>
        internal static FlattenedUnionFind? FromPairs(ReadOnlySpan<uint> keys, ReadOnlySpan<uint> values)
        {
            if (keys.Length != values.Length)
                throw new ArgumentException("Every key needs a value.", nameof(values));

            if (keys.IsEmpty)
                return null;

            var lo = keys[0];
            var hi = keys[0];
            foreach (var key in keys)
            {
                lo = Math.Min(lo, key);
                hi = Math.Max(hi, key);
            }

            var range = (long)hi - lo + 1;
            if (range <= (long)DenseFactor * keys.Length)
            {
                var array = GC.AllocateUninitializedArray<uint>((int)range);
                IdentityFill.Fill(array, lo);
                for (var i = 0; i < keys.Length; i++)
                    array[keys[i] - lo] = values[i];

                return new FlattenedUnionFind(array, lo, keys.Length);
            }

            var table = new RawHashTable<IdEntry>(keys.Length);
            for (var i = 0; i < keys.Length; i++)
                table.InsertUnique(FxHasher.Hash(keys[i]), new IdEntry(keys[i], values[i]), default(IdEntryHasher));

            return new FlattenedUnionFind(table);
        }

        /// <summary>
        /// Maps a root-canonical id to its canonical id in the flattened chain; the identity outside the map's domain.
        /// One bounds check and one load (dense) or one probe (hash).
        /// </summary>
        /// <param name="id">An id canonical in the root.</param>
        /// <returns>The canonical id.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Map(uint id)
        {
            if (dense is { } d)
            {
                var index = id - min;
                return index < (uint)d.Length ? d[index] : id;
            }

            var match = new IdEntryMatch(id);
            return hash!.TryFind(FxHasher.Hash(id), ref match, out var entry) ? entry.Payload : id;
        }

        /// <summary>
        /// Adds every id the map moves to <paramref name="into"/>, for flattening a deeper chain on top of this one.
        /// </summary>
        /// <param name="into">The list to add to.</param>
        internal void CollectKeys(List<uint> into)
        {
            ArgumentNullException.ThrowIfNull(into);
            if (dense is { } d)
            {
                for (var i = 0; i < d.Length; i++)
                    if (d[i] != min + (uint)i)
                        into.Add(min + (uint)i);

                return;
            }

            foreach (ref var entry in hash!)
                into.Add(entry.Id);
        }

    }

}
