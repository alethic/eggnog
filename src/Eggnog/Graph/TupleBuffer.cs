using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog
{

    /// <summary>
    /// A set of fixed-width <see cref="uint"/> tuples, which query and split-matcher collectors fill during an engine
    /// call, and which <see cref="Scope.Rows(TableHandle)"/> fills from a table. <see cref="Add"/> is thread-safe (rule
    /// tasks may run in parallel) and drops duplicates; after <see cref="SortByCells"/> the indexer reads in lexicographic
    /// order of the cells, after <see cref="SortByContent"/> in content order, and otherwise in insertion order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tuples are stored row-major in one array, and found by an open-addressing table of tuple numbers (linear probing,
    /// at most half full). Every array is rented from <see cref="ArrayPool{T}.Shared"/> and returned by
    /// <see cref="Dispose"/>, so a call that fills a buffer allocates nothing per tuple once the pool holds arrays of the
    /// sizes it needs. <see cref="Add"/> costs one hash and an expected O(1)
    /// compares; growth rehashes, amortized O(1) per tuple.
    /// </para>
    /// <para>
    /// Reading (<see cref="Count"/>, the indexer, <see cref="Cells"/>, the sorts) is for the thread that owns the buffer
    /// once the engine call has ended. Spans read from it are valid until the next change or <see cref="Dispose"/>.
    /// </para>
    /// </remarks>
    internal sealed class TupleBuffer : IDisposable
    {

        /// <summary>
        /// The smallest open-addressing table.
        /// </summary>
        const int MinimumSlots = 16;

        /// <summary>
        /// The smallest cell array.
        /// </summary>
        const int MinimumCells = 64;

        readonly object sync = new();
        uint[] cells = Array.Empty<uint>();
        int[] slots = Array.Empty<int>();
        int slotMask = -1;
        int[]? order;
        int count;

        /// <summary>
        /// Initializes an empty buffer of tuples of <paramref name="width"/> cells. Allocates no array until the first
        /// tuple.
        /// </summary>
        /// <param name="width">The number of cells per tuple; may be zero.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is negative.</exception>
        public TupleBuffer(int width)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(width);
            Width = width;
        }

        /// <summary>
        /// Gets the number of cells per tuple.
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// Gets the number of distinct tuples.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets the tuple at <paramref name="index"/>: in the order of the last sort, or in insertion order.
        /// </summary>
        /// <param name="index">The index, from 0.</param>
        /// <returns>The tuple's cells, valid until the buffer changes.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not below <see cref="Count"/>.</exception>
        public ReadOnlySpan<uint> this[int index]
        {
            get
            {
                if ((uint)index >= (uint)count)
                    throw new ArgumentOutOfRangeException(nameof(index), index, $"The buffer holds {count} tuples.");

                var tuple = order is null ? index : order[index];
                return new ReadOnlySpan<uint>(cells, tuple * Width, Width);
            }
        }

        /// <summary>
        /// Gets the cells of every tuple, row-major in insertion order, to change in place (canonicalizing classes). After a
        /// change that may make two tuples equal, call <see cref="Deduplicate"/>.
        /// </summary>
        public Span<uint> Cells => new(cells, 0, count * Width);

        /// <summary>
        /// Adds a tuple unless an equal one is present. Thread-safe. Forgets the last sort.
        /// </summary>
        /// <param name="tuple">The tuple; copied.</param>
        /// <returns><c>false</c> if an equal tuple was already present.</returns>
        /// <exception cref="ArgumentException">The tuple's length is not <see cref="Width"/>.</exception>
        public bool Add(ReadOnlySpan<uint> tuple)
        {
            if (tuple.Length != Width)
                throw new ArgumentException($"A tuple of this buffer has {Width} cells, not {tuple.Length}.", nameof(tuple));

            lock (sync)
            {
                ForgetOrder();
                if (Width == 0)
                {
                    // Every tuple of width zero is the empty tuple.
                    if (count > 0)
                        return false;

                    count = 1;
                    return true;
                }

                if ((count + 1) * 2 > slotMask + 1)
                    Rehash(Math.Max(MinimumSlots, (slotMask + 1) * 2));

                var slot = Find(tuple, out var found);
                if (found)
                    return false;

                EnsureCells((count + 1) * Width);
                tuple.CopyTo(new Span<uint>(cells, count * Width, Width));
                count++;
                slots[slot] = count;
                return true;
            }
        }

        /// <summary>
        /// Drops every tuple equal to an earlier one, keeping insertion order: after <see cref="Cells"/> was changed in
        /// place. Forgets the last sort. O(n).
        /// </summary>
        public void Deduplicate()
        {
            lock (sync)
            {
                ForgetOrder();
                if (Width == 0 || count == 0)
                    return;

                Array.Clear(slots, 0, slotMask + 1);
                var kept = 0;
                for (var t = 0; t < count; t++)
                {
                    var tuple = new ReadOnlySpan<uint>(cells, t * Width, Width);
                    var slot = Find(tuple, out var found);
                    if (found)
                        continue;

                    if (kept != t)
                        tuple.CopyTo(new Span<uint>(cells, kept * Width, Width));

                    kept++;
                    slots[slot] = kept;
                }

                count = kept;
            }
        }

        /// <summary>
        /// Orders the indexer lexicographically by the tuples' cells. O(n log n) compares; the cells do not move.
        /// </summary>
        public void SortByCells() => Sort(null);

        /// <summary>
        /// Orders the indexer in content order: column by column, each by its column type's order (strings ordinal,
        /// numbers numeric, sets lexicographic, classes by id). Distinct cells that a type compares as equal fall back to
        /// the cells' order, so the order is total and deterministic. O(n log n) compares, each decoding the cells it
        /// compares; the cells do not move.
        /// </summary>
        /// <param name="columns">The column types, one per cell of a tuple.</param>
        /// <exception cref="ArgumentException">The number of column types is not <see cref="Width"/>.</exception>
        public void SortByContent(IReadOnlyList<ColumnType> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);
            if (columns.Count != Width)
                throw new ArgumentException($"A tuple of this buffer has {Width} cells, not {columns.Count}.", nameof(columns));

            Sort(columns);
        }

        /// <summary>
        /// Removes every tuple; keeps the arrays.
        /// </summary>
        public void Clear()
        {
            lock (sync)
            {
                ForgetOrder();
                if (slotMask >= 0)
                    Array.Clear(slots, 0, slotMask + 1);

                count = 0;
            }
        }

        /// <summary>
        /// Returns the arrays to the pool and empties the buffer, which stays usable. Spans read before must not be used
        /// afterwards.
        /// </summary>
        public void Dispose()
        {
            lock (sync)
            {
                ForgetOrder();
                if (cells.Length > 0)
                    ArrayPool<uint>.Shared.Return(cells);

                if (slots.Length > 0)
                    ArrayPool<int>.Shared.Return(slots);

                cells = Array.Empty<uint>();
                slots = Array.Empty<int>();
                slotMask = -1;
                count = 0;
            }
        }

        /// <summary>
        /// Sorts the indexer's order: by content when <paramref name="columns"/> is given, else by cells.
        /// </summary>
        void Sort(IReadOnlyList<ColumnType>? columns)
        {
            lock (sync)
            {
                ForgetOrder();
                var sorted = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
                for (var i = 0; i < count; i++)
                    sorted[i] = i;

                if (Width > 0 && count > 1)
                    new Span<int>(sorted, 0, count).Sort(new TupleOrder(cells, Width, columns));

                order = sorted;
            }
        }

        /// <summary>
        /// Returns the order of the last sort to the pool: the indexer reads in insertion order again.
        /// </summary>
        void ForgetOrder()
        {
            if (order is null)
                return;

            ArrayPool<int>.Shared.Return(order);
            order = null;
        }

        /// <summary>
        /// Finds the slot of <paramref name="tuple"/>: its own when <paramref name="found"/>, else the empty slot where it
        /// belongs. The table must have an empty slot.
        /// </summary>
        int Find(ReadOnlySpan<uint> tuple, out bool found)
        {
            var slot = Hash(tuple) & slotMask;
            while (true)
            {
                var entry = slots[slot];
                if (entry == 0)
                {
                    found = false;
                    return slot;
                }

                if (tuple.SequenceEqual(new ReadOnlySpan<uint>(cells, (entry - 1) * Width, Width)))
                {
                    found = true;
                    return slot;
                }

                slot = (slot + 1) & slotMask;
            }
        }

        /// <summary>
        /// Replaces the open-addressing table by one of <paramref name="size"/> slots, a power of two, and reinserts every
        /// tuple.
        /// </summary>
        void Rehash(int size)
        {
            Debug.Assert(BitOperations.IsPow2(size), "the table's size is a power of two");

            // The pool may hand out a longer array; only its first size slots are used.
            var rented = ArrayPool<int>.Shared.Rent(size);
            Array.Clear(rented, 0, size);
            if (slots.Length > 0)
                ArrayPool<int>.Shared.Return(slots);

            slots = rented;
            slotMask = size - 1;
            for (var t = 0; t < count; t++)
            {
                var slot = Find(new ReadOnlySpan<uint>(cells, t * Width, Width), out _);
                slots[slot] = t + 1;
            }
        }

        /// <summary>
        /// Makes room for <paramref name="needed"/> cells, keeping the tuples.
        /// </summary>
        void EnsureCells(int needed)
        {
            if (needed <= cells.Length)
                return;

            var rented = ArrayPool<uint>.Shared.Rent(Math.Max(needed, Math.Max(MinimumCells, cells.Length * 2)));
            new ReadOnlySpan<uint>(cells, 0, count * Width).CopyTo(rented);
            if (cells.Length > 0)
                ArrayPool<uint>.Shared.Return(cells);

            cells = rented;
        }

        /// <summary>
        /// Hashes a tuple: FNV-1a over its cells, then a final mix so the low bits that pick a slot depend on every cell.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Hash(ReadOnlySpan<uint> tuple)
        {
            var hash = 2166136261u;
            foreach (var cell in tuple)
                hash = (hash ^ cell) * 16777619u;

            hash ^= hash >> 16;
            hash *= 0x85EBCA6Bu;
            hash ^= hash >> 13;
            return unchecked((int)hash);
        }

    }

}
