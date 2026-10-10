using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Enumerates the entries of a <see cref="RawHashTable{TEntry}"/> in ascending slot order, a control group at a
    /// time (hashbrown's <c>RawIter</c>): one SIMD load per 16 slots, then one bit-scan per entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Current"/> is a reference, so <c>foreach (ref var entry in table)</c> can update entries in place.
    /// The table must not be modified otherwise during enumeration, except that the current entry may be removed with
    /// <see cref="RawHashTable{TEntry}.RemoveAt"/> (as hashbrown's <c>RawIter</c> allows erasing the item it just
    /// yielded): that leaves the entries still ahead unchanged. Removing entries that lie ahead makes
    /// <see cref="MoveNext"/> throw <see cref="InvalidOperationException"/> when it runs out of slots.
    /// </para>
    /// <para>
    /// Like hashbrown's <c>RawIter</c>, the enumerator counts down the entries still to yield and stops as soon as
    /// that count reaches zero: enumerating an empty table is O(1) whatever its slot count, and enumerating a sparse
    /// one stops at its last entry instead of scanning the groups after it.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntry">The entry type.</typeparam>
    internal struct RawHashTableEnumerator<TEntry>
        where TEntry : struct
    {

        readonly byte[] ctrl;
        readonly TEntry[] entries;
        int groupBase;
        ulong bits;
        int slot;
        int remaining;

        /// <summary>
        /// Initializes a new instance over a table's arrays.
        /// </summary>
        /// <param name="ctrl">The table's control bytes.</param>
        /// <param name="entries">The table's slots.</param>
        /// <param name="items">The number of full slots, the count of entries to yield.</param>
        internal RawHashTableEnumerator(byte[] ctrl, TEntry[] entries, int items)
        {
            this.ctrl = ctrl;
            this.entries = entries;
            groupBase = -ControlGroup.Width;
            bits = 0;
            slot = -1;
            remaining = items;
        }

        /// <summary>
        /// Gets a reference to the current entry.
        /// </summary>
        public readonly ref TEntry Current => ref entries[slot];

        /// <summary>
        /// Gets the slot of the current entry.
        /// </summary>
        public readonly int Slot => slot;

        /// <summary>
        /// Advances to the next full slot.
        /// </summary>
        /// <returns><c>false</c> when there are no more entries.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            // hashbrown's RawIter::next: once every entry was yielded, stop without scanning the remaining groups.
            if (remaining == 0)
                return false;

            while (bits == 0)
            {
                groupBase += ControlGroup.Width;

                // While entries remain, an unmodified table has a full slot ahead, so this never fails; it keeps the
                // unchecked group load below inside the array if the table was modified during enumeration. Aligned
                // groups never reach the mirror bytes; in a table smaller than a group, the bytes past the last slot
                // are EMPTY.
                if (groupBase >= entries.Length)
                    ThrowModified();

                bits = ControlGroup.MatchFull(ControlGroup.Load(ref MemoryMarshal.GetArrayDataReference(ctrl), groupBase));
            }

            slot = groupBase + ControlGroup.LowestSetBit(bits);
            bits = ControlGroup.RemoveLowestBit(bits);
            remaining--;
            return true;
        }

        /// <summary>
        /// Reports a table that lost entries during enumeration.
        /// </summary>
        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowModified() => throw new InvalidOperationException("The hash table was modified during enumeration.");

    }

}
