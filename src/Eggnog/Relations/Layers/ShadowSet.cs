using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The ancestor container ids one child context's layer of a container type shadows
    /// (<see cref="ContainerLayer{TContainer}"/>): a set of ids, none of them local to the context, that only grows while
    /// its owner lives. Every ancestor entry under a shadowed id is hidden from the context and its descendants, and the
    /// layer's own environment holds the entries the context sees under it (<see cref="ContainerLayers"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Representation. One open-addressed table of ids with linear probing, a power of two long and at most half full,
    /// <see cref="Empty"/> in the free slots; ids never reach it (<see cref="UnionFind.MaxId"/>). The table starts
    /// empty and unallocated, so a layer that shadows nothing answers with one load.
    /// </para>
    /// <para>
    /// Threads. Only the owning context's domain thread adds, and nothing adds once the set is frozen
    /// (<see cref="Freeze"/>). Readers on any thread take no lock: an add that needs no growth writes one free slot,
    /// which a probe reads either free or filled, and since nothing is ever removed, a probe for an id added earlier
    /// still meets it before a free slot; an add that needs growth fills a new table with the ids so far, publishes it
    /// with a volatile write, so a reader keeps a consistent table, and then writes the new id into it as any add does.
    /// </para>
    /// <para>
    /// Cost: <see cref="Contains"/> and <see cref="Add"/> O(1) expected; growth O(n). Memory: 8 to 16 bytes per id.
    /// </para>
    /// </remarks>
    internal sealed class ShadowSet
    {

        /// <summary>
        /// The content of a free slot.
        /// </summary>
        const uint Empty = uint.MaxValue;

        /// <summary>
        /// The length of the first table.
        /// </summary>
        const int FirstLength = 8;

        /// <summary>
        /// The table, a power of two long, or empty. Replaced whole on growth; a slot is written once, from free to an id.
        /// </summary>
        uint[] slots = Array.Empty<uint>();

        /// <summary>
        /// The number of ids; written by the domain thread.
        /// </summary>
        int count;

        /// <summary>
        /// Whether the set is frozen: nothing is added any more.
        /// </summary>
        volatile bool frozen;

        /// <summary>
        /// Gets the number of ids. Exact on the domain thread and once frozen; a snapshot otherwise.
        /// </summary>
        public int Count => Volatile.Read(ref count);

        /// <summary>
        /// Gets whether the set is frozen (<see cref="Freeze"/>).
        /// </summary>
        public bool IsFrozen => frozen;

        /// <summary>
        /// Gets whether <paramref name="id"/> is in the set. Thread-safe and lock-free: one volatile read of the table and
        /// an expected-O(1) probe; one load when the set is empty.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if the id is shadowed.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(uint id)
        {
            var table = Volatile.Read(ref slots);
            if (table.Length == 0)
                return false;

            var mask = table.Length - 1;
            for (var i = (int)FxHasher.Hash(id) & mask; ; i = (i + 1) & mask)
            {
                var slot = Volatile.Read(ref table[i]);
                if (slot == id)
                    return true;

                if (slot == Empty)
                    return false;
            }
        }

        /// <summary>
        /// Adds <paramref name="id"/>. Domain thread only.
        /// </summary>
        /// <param name="id">The id, at most <see cref="UnionFind.MaxId"/>.</param>
        /// <returns><c>true</c> if it was not in the set.</returns>
        /// <exception cref="InvalidOperationException">The set is frozen.</exception>
        public bool Add(uint id)
        {
            Debug.Assert(id != Empty, "an id equal to the free-slot marker");
            if (frozen)
                throw new InvalidOperationException("A frozen context's shadowed container ids never change.");

            if (Contains(id))
                return false;

            var table = slots;
            if (2 * (count + 1) > table.Length)
            {
                table = Grow(table);

                // Release: a reader that sees the new table sees every id copied into it.
                Volatile.Write(ref slots, table);
            }

            Place(table, id);
            Volatile.Write(ref count, count + 1);
            return true;
        }

        /// <summary>
        /// Freezes the set: nothing is added any more, and every reader may keep what it reads. Domain thread only, as
        /// part of the owning context's freeze.
        /// </summary>
        public void Freeze() => frozen = true;

        /// <summary>
        /// Appends every id in the set to <paramref name="destination"/>, in no particular order. For the invariant
        /// checker; exact on the domain thread and once frozen.
        /// </summary>
        /// <param name="destination">Receives the ids.</param>
        public void CopyTo(List<uint> destination)
        {
            foreach (var slot in Volatile.Read(ref slots))
                if (slot != Empty)
                    destination.Add(slot);
        }

        /// <summary>
        /// Makes a table twice as long as <paramref name="table"/> (or the first one), holding its ids.
        /// </summary>
        static uint[] Grow(uint[] table)
        {
            var grown = new uint[Math.Max(FirstLength, table.Length * 2)];
            Array.Fill(grown, Empty);
            foreach (var slot in table)
                if (slot != Empty)
                    Place(grown, slot);

            return grown;
        }

        /// <summary>
        /// Writes <paramref name="id"/> into the first free slot of its probe sequence in <paramref name="table"/>,
        /// which has one and does not hold the id.
        /// </summary>
        static void Place(uint[] table, uint id)
        {
            var mask = table.Length - 1;
            var i = (int)FxHasher.Hash(id) & mask;
            while (table[i] != Empty)
                i = (i + 1) & mask;

            Volatile.Write(ref table[i], id);
        }

    }

}
