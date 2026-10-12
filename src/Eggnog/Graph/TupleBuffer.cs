using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A set of fixed-width <see cref="uint"/> tuples, which query and split-matcher collectors fill during an engine
    /// call. <see cref="Add"/> is thread-safe (rule tasks may run in parallel) and drops duplicates; after
    /// <see cref="SortByContent"/>, indexing is in lexicographic order of the cells.
    /// </summary>
    /// <remarks>
    /// Tuples are stored row-major in one growable array; duplicates are found through a hash of each tuple. Add costs
    /// one hash and, on a collision, one compare per tuple with the same hash. Reading (<see cref="Count"/>, the
    /// indexer, <see cref="SortByContent"/>) is for the thread that owns the buffer once the engine call has ended.
    /// </remarks>
    internal sealed class TupleBuffer
    {

        readonly object sync = new();
        readonly Dictionary<int, List<int>> byHash = new();
        uint[] cells = Array.Empty<uint>();
        int count;

        /// <summary>
        /// Initializes an empty buffer of tuples of <paramref name="width"/> cells.
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
        /// Gets the tuple at <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The index, from 0.</param>
        /// <returns>The tuple's cells.</returns>
        public ReadOnlySpan<uint> this[int index]
        {
            get
            {
                if ((uint)index >= (uint)count)
                    throw new ArgumentOutOfRangeException(nameof(index), index, $"The buffer holds {count} tuples.");

                return new ReadOnlySpan<uint>(cells, index * Width, Width);
            }
        }

        /// <summary>
        /// Adds a tuple unless an equal one is present. Thread-safe.
        /// </summary>
        /// <param name="tuple">The tuple; copied.</param>
        /// <returns><c>false</c> if an equal tuple was already present.</returns>
        /// <exception cref="ArgumentException">The tuple's length is not <see cref="Width"/>.</exception>
        public bool Add(ReadOnlySpan<uint> tuple)
        {
            if (tuple.Length != Width)
                throw new ArgumentException($"A tuple of this buffer has {Width} cells, not {tuple.Length}.", nameof(tuple));

            var hash = Hash(tuple);
            lock (sync)
            {
                if (byHash.TryGetValue(hash, out var candidates))
                {
                    foreach (var candidate in candidates)
                        if (tuple.SequenceEqual(new ReadOnlySpan<uint>(cells, candidate * Width, Width)))
                            return false;
                }
                else
                {
                    candidates = new List<int>(1);
                    byHash.Add(hash, candidates);
                }

                if ((count + 1) * Width > cells.Length)
                    Array.Resize(ref cells, Math.Max(Width * 4, cells.Length * 2));

                tuple.CopyTo(new Span<uint>(cells, count * Width, Width));
                candidates.Add(count);
                count++;
                return true;
            }
        }

        /// <summary>
        /// Sorts the tuples lexicographically by their cells. O(n log n) compares.
        /// </summary>
        public void SortByContent()
        {
            lock (sync)
            {
                var order = new int[count];
                for (var i = 0; i < order.Length; i++)
                    order[i] = i;

                var width = Width;
                var source = cells;
                Array.Sort(order, (a, b) => new ReadOnlySpan<uint>(source, a * width, width).SequenceCompareTo(new ReadOnlySpan<uint>(source, b * width, width)));

                var sorted = new uint[Math.Max(cells.Length, 0)];
                for (var i = 0; i < order.Length; i++)
                    new ReadOnlySpan<uint>(source, order[i] * width, width).CopyTo(new Span<uint>(sorted, i * width, width));

                cells = sorted;
                byHash.Clear();
                for (var i = 0; i < count; i++)
                {
                    var hash = Hash(new ReadOnlySpan<uint>(cells, i * width, width));
                    if (byHash.TryGetValue(hash, out var list) == false)
                        byHash.Add(hash, list = new List<int>(1));

                    list.Add(i);
                }
            }
        }

        /// <summary>
        /// Removes every tuple.
        /// </summary>
        public void Clear()
        {
            lock (sync)
            {
                byHash.Clear();
                count = 0;
            }
        }

        /// <summary>
        /// Hashes a tuple (FNV-1a over its cells).
        /// </summary>
        static int Hash(ReadOnlySpan<uint> tuple)
        {
            var hash = 2166136261u;
            foreach (var cell in tuple)
                hash = (hash ^ cell) * 16777619u;

            return unchecked((int)hash);
        }

    }

}
