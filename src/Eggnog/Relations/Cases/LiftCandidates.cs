using System;
using System.Diagnostics;

namespace Eggnog.Relations
{

    /// <summary>
    /// A deduplicating arena of fixed-width entries for the lift: the seed case's source rows, and the parent keys they
    /// stand for. Entries are equal when their first <see cref="KeyWidth"/> cells are; the first entry added with a key
    /// is kept.
    /// </summary>
    /// <remarks>
    /// Row-major cells in one array and an open-addressing table of entry indexes (linear probing, at most half full),
    /// hashed with <see cref="FxHasher"/>. <see cref="Reset"/> keeps the storage, so a lift reuses one arena for every
    /// table and round: after warm-up, adding allocates nothing. <see cref="Reset"/> costs what the arena last held, not
    /// its capacity: after one large table, the slot table stays large, and clearing all of it for every following
    /// table and round would make a round cost O(tables × the largest table ever seen). Not thread-safe.
    /// </remarks>
    internal sealed class LiftCandidates
    {

        uint[] cells = new uint[64];
        int[] slots = new int[16];
        int width = 1;
        int keyWidth = 1;
        int count;

        /// <summary>
        /// Gets the number of entries.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets the cells per entry.
        /// </summary>
        public int Width => width;

        /// <summary>
        /// Gets the leading cells that identify an entry.
        /// </summary>
        public int KeyWidth => keyWidth;

        /// <summary>
        /// Empties the arena and sets the shape of its entries. O(min(slots, entries × key width)): when the entries held
        /// are few against the slot table, only their slots are cleared.
        /// </summary>
        /// <param name="width">The cells per entry; at least 1.</param>
        /// <param name="keyWidth">The leading cells that identify an entry; from 0 to <paramref name="width"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException">A width is out of range.</exception>
        public void Reset(int width, int keyWidth)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(keyWidth);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(keyWidth, width);
            ClearSlots();
            this.width = width;
            this.keyWidth = keyWidth;
            count = 0;
        }

        /// <summary>
        /// Adds <paramref name="entry"/> unless an entry with the same key is present. Expected O(width).
        /// </summary>
        /// <param name="entry">The entry; exactly <see cref="Width"/> cells.</param>
        /// <returns><c>true</c> if added.</returns>
        /// <exception cref="ArgumentException">The entry's width is wrong.</exception>
        public bool Add(ReadOnlySpan<uint> entry)
        {
            if (entry.Length != width)
                throw new ArgumentException($"An entry has {width} cells, not {entry.Length}.", nameof(entry));

            if (2 * (count + 1) > slots.Length)
                Grow();

            var key = entry.Slice(0, keyWidth);
            var mask = slots.Length - 1;
            var slot = (int)FxHasher.Hash(key) & mask;
            while (slots[slot] != 0)
            {
                if (KeyAt(slots[slot] - 1).SequenceEqual(key))
                    return false;

                slot = (slot + 1) & mask;
            }

            var at = count * width;
            if (at + width > cells.Length)
                Array.Resize(ref cells, Math.Max(cells.Length * 2, at + width));

            entry.CopyTo(cells.AsSpan(at, width));
            slots[slot] = ++count;
            return true;
        }

        /// <summary>
        /// Gets entry <paramref name="index"/>; valid until the next <see cref="Add"/> or <see cref="Reset"/>.
        /// </summary>
        /// <param name="index">An entry index, below <see cref="Count"/>.</param>
        /// <returns>The entry's cells.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
        public ReadOnlySpan<uint> this[int index]
        {
            get
            {
                if ((uint)index >= (uint)count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                return new ReadOnlySpan<uint>(cells, index * width, width);
            }
        }

        /// <summary>
        /// Gets the key cells of entry <paramref name="index"/>.
        /// </summary>
        ReadOnlySpan<uint> KeyAt(int index) => new(cells, index * width, keyWidth);

        /// <summary>
        /// Zeroes the slot table under the current shape: in full when the entries fill a good part of it, otherwise
        /// entry by entry, finding each one's slot by its hash (every entry is present, so each probe ends at its slot).
        /// </summary>
        void ClearSlots()
        {
            if (count == 0)
                return;

            if (8 * count >= slots.Length)
            {
                Array.Clear(slots);
                return;
            }

            var mask = slots.Length - 1;
            for (var i = 0; i < count; i++)
            {
                var slot = (int)FxHasher.Hash(KeyAt(i)) & mask;
                while (slots[slot] != i + 1)
                    slot = (slot + 1) & mask;

                slots[slot] = 0;
            }
        }

        /// <summary>
        /// Doubles the slot table and re-inserts every entry.
        /// </summary>
        void Grow()
        {
            var grown = new int[slots.Length * 2];
            var mask = grown.Length - 1;
            for (var i = 0; i < count; i++)
            {
                var slot = (int)FxHasher.Hash(KeyAt(i)) & mask;
                while (grown[slot] != 0)
                    slot = (slot + 1) & mask;

                grown[slot] = i + 1;
            }

            slots = grown;
            Debug.Assert(2 * count <= slots.Length);
        }

    }

}
