using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The rows of one ancestor layer that one child context hides: a set of row ids of that layer, within one of its
    /// generations, that only grows while its owner lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Representation. A sealed part, which readers probe, and an unsorted tail, which <see cref="Add"/> appends to. The
    /// sealed part is an exact-size sorted <c>int[]</c> until the set holds at least <see cref="MinBitsetCount"/> rows and
    /// more than one row in <see cref="BitsetDivisor"/> of its layer, and a <c>ulong[]</c> bitset from then on (it never
    /// switches back): whichever is smaller. <see cref="Seal"/> merges the tail into a new sealed array (or bitset) and
    /// publishes it by reference, so a reader holding the old one keeps a consistent snapshot.
    /// </para>
    /// <para>
    /// Sealing discipline. Hides happen only on the owning context's domain thread, inside its merge or right before
    /// the merge of the same pass, and every merge epilogue seals what it touched; so outside a merge of its owner
    /// every set is sealed, and the read paths (<see cref="ContainsSealed"/>, <see cref="HiddenCursor"/>,
    /// <see cref="Union"/>) agree with the write path's <see cref="Contains"/>, which also reads the tail. A frozen
    /// context's sets never change, and may be read from any thread.
    /// </para>
    /// <para>
    /// Cost: <see cref="Add"/> O(1) expected; <see cref="ContainsSealed"/> O(log n), or one bit test; <see cref="Seal"/>
    /// O(n + tail log tail). Memory: 4 bytes per row, or one bit per row of the layer as a bitset.
    /// </para>
    /// </remarks>
    internal sealed class HiddenSet
    {

        /// <summary>
        /// The fewest rows a set holds before it may become a bitset.
        /// </summary>
        public const int MinBitsetCount = 64;

        /// <summary>
        /// A set becomes a bitset once it holds more than its layer's row count divided by this (and at least
        /// <see cref="MinBitsetCount"/> rows): the point where the bitset is the smaller representation.
        /// </summary>
        public const int BitsetDivisor = 32;

        readonly int generation;

        /// <summary>
        /// The sealed rows, sorted ascending and distinct, exactly sized; empty when the set is a bitset. Replaced whole.
        /// </summary>
        int[] sorted = Array.Empty<int>();

        /// <summary>
        /// The sealed rows as a bitset, or <c>null</c> while the set is a sorted array. Replaced whole.
        /// </summary>
        ulong[]? bits;

        /// <summary>
        /// The rows added since the last seal; <c>null</c> or empty when the set is sealed.
        /// </summary>
        HashSet<int>? tail;

        int count;
        int version;
        int maxRow = -1;

        /// <summary>
        /// Initializes an empty, sealed set over generation <paramref name="generation"/> of its layer.
        /// </summary>
        /// <param name="generation">The layer's <see cref="TableVersion.Major"/>; row ids are valid only within it.</param>
        public HiddenSet(int generation)
        {
            this.generation = generation;
        }

        /// <summary>
        /// Gets the layer generation the row ids belong to.
        /// </summary>
        public int Generation => generation;

        /// <summary>
        /// Gets the number of rows in the set, tail included.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets a counter moved by every effective <see cref="Add"/>.
        /// </summary>
        public int Version => version;

        /// <summary>
        /// Gets whether the tail is empty, so the sealed part holds every row and readers may probe it.
        /// </summary>
        public bool IsSealed => tail is null || tail.Count == 0;

        /// <summary>
        /// Gets whether the sealed part is a bitset.
        /// </summary>
        public bool IsBitset => bits is not null;

        /// <summary>
        /// Gets the sealed rows, sorted ascending, for compiled kernels; <c>null</c> for a bitset. Exactly
        /// <see cref="SortedCount"/> long.
        /// </summary>
        internal int[]? SortedRows => bits is null ? sorted : null;

        /// <summary>
        /// Gets the number of sealed rows in <see cref="SortedRows"/>; zero for a bitset.
        /// </summary>
        internal int SortedCount => bits is null ? sorted.Length : 0;

        /// <summary>
        /// Gets the sealed bitset (bit <c>r</c> set when row <c>r</c> is hidden), or <c>null</c> for a sorted array.
        /// </summary>
        internal ulong[]? Bits => bits;

        /// <summary>
        /// Adds <paramref name="row"/>. A sealed set reopens: the row goes to the tail until the next <see cref="Seal"/>.
        /// Owner's domain thread only.
        /// </summary>
        /// <param name="row">A row id of the layer, below <paramref name="layerRowCount"/>.</param>
        /// <param name="layerRowCount">The layer's physical row count.</param>
        /// <returns><c>false</c> if the row was in the set already.</returns>
        public bool Add(int row, int layerRowCount)
        {
            Debug.Assert(row >= 0 && row < layerRowCount, "a hidden row lies outside its layer");
            if (SealedContains(row))
                return false;

            tail ??= new HashSet<int>();
            if (tail.Add(row) == false)
                return false;

            count++;
            version++;
            if (row > maxRow)
                maxRow = row;

            return true;
        }

        /// <summary>
        /// Gets whether <paramref name="row"/> is in the set, sealed or not: the write path's probe.
        /// </summary>
        /// <param name="row">A row id.</param>
        /// <returns><c>true</c> if hidden.</returns>
        public bool Contains(int row) => SealedContains(row) || (tail is { Count: > 0 } t && t.Contains(row));

        /// <summary>
        /// Gets whether <paramref name="row"/> is in the sealed part: the read paths' probe, which requires a sealed set.
        /// </summary>
        /// <param name="row">A row id.</param>
        /// <returns><c>true</c> if hidden.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsSealed(int row)
        {
            Debug.Assert(IsSealed, "a reader probed a hidden set that is not sealed");
            return SealedContains(row);
        }

        /// <summary>
        /// Merges the tail into the sealed part, switching to a bitset when that is smaller, and publishes the result.
        /// Owner's domain thread only. O(n + tail log tail); nothing when sealed.
        /// </summary>
        /// <param name="layerRowCount">The layer's physical row count.</param>
        public void Seal(int layerRowCount)
        {
            if (IsSealed)
                return;

            var added = tail!;
            if (bits is null && ShouldBeBitset(count, layerRowCount))
            {
                var next = new ulong[WordsFor(Math.Max(layerRowCount, maxRow + 1))];
                foreach (var row in sorted)
                    next[row >> 6] |= 1UL << row;

                foreach (var row in added)
                    next[row >> 6] |= 1UL << row;

                sorted = Array.Empty<int>();
                bits = next;
            }
            else if (bits is { } current)
            {
                var next = new ulong[Math.Max(current.Length, WordsFor(maxRow + 1))];
                Array.Copy(current, next, current.Length);
                foreach (var row in added)
                    next[row >> 6] |= 1UL << row;

                bits = next;
            }
            else
            {
                var extra = new int[added.Count];
                added.CopyTo(extra);
                Array.Sort(extra);
                sorted = MergeSorted(sorted, extra);
            }

            added.Clear();
        }

        /// <summary>
        /// Builds the union of <paramref name="sets"/>, all sealed and over one generation of one layer, as a new
        /// sealed set (the live sets of the non-frozen levels of a view, or a frozen fold). O(Σ n), or O(layer / 64)
        /// for bitsets.
        /// </summary>
        /// <param name="sets">The sets; at least one.</param>
        /// <param name="layerRowCount">The layer's physical row count.</param>
        /// <returns>The union.</returns>
        public static HiddenSet Union(ReadOnlySpan<HiddenSet> sets, int layerRowCount)
        {
            if (sets.IsEmpty)
                throw new ArgumentException("A union needs at least one set.", nameof(sets));

            var generation = sets[0].generation;
            var total = 0;
            var anyBits = false;
            var maxRow = -1;
            foreach (var set in sets)
            {
                Debug.Assert(set.IsSealed, "a hidden set was unioned before it was sealed");
                Debug.Assert(set.generation == generation, "hidden sets of different generations were unioned");
                total += set.count;
                anyBits |= set.bits is not null;
                maxRow = Math.Max(maxRow, set.maxRow);
            }

            var result = new HiddenSet(generation) { maxRow = maxRow };
            if (anyBits || ShouldBeBitset(total, layerRowCount))
            {
                var words = WordsFor(Math.Max(layerRowCount, maxRow + 1));
                foreach (var set in sets)
                    if (set.bits is { } b)
                        words = Math.Max(words, b.Length);

                var union = new ulong[words];
                foreach (var set in sets)
                {
                    if (set.bits is { } b)
                    {
                        for (var i = 0; i < b.Length; i++)
                            union[i] |= b[i];
                    }
                    else
                    {
                        foreach (var row in set.sorted)
                            union[row >> 6] |= 1UL << row;
                    }
                }

                var count = 0;
                foreach (var word in union)
                    count += BitOperations.PopCount(word);

                result.bits = union;
                result.count = count;
                return result;
            }

            var merged = sets[0].sorted;
            for (var i = 1; i < sets.Length; i++)
                merged = MergeSorted(merged, sets[i].sorted);

            result.sorted = merged;
            result.count = merged.Length;
            return result;
        }

        /// <summary>
        /// Appends every row of the set, tail included, to <paramref name="rows"/>, ascending. For checkers and tests.
        /// </summary>
        /// <param name="rows">Receives the rows.</param>
        public void CopyTo(List<int> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var start = rows.Count;
            if (bits is { } b)
            {
                for (var word = 0; word < b.Length; word++)
                {
                    var w = b[word];
                    while (w != 0)
                    {
                        rows.Add((word << 6) + BitOperations.TrailingZeroCount(w));
                        w &= w - 1;
                    }
                }
            }
            else
            {
                rows.AddRange(sorted);
            }

            if (tail is { Count: > 0 } t)
            {
                rows.AddRange(t);
                rows.Sort(start, rows.Count - start, Comparer<int>.Default);
            }
        }

        /// <summary>
        /// Counts the rows of the set whose cell in <paramref name="data"/> is live (first cell not
        /// <see cref="Value.StaleRep"/>) and that <paramref name="excluded"/> does not hold: for a view's exact visible
        /// count. Requires a sealed set. O(n).
        /// </summary>
        /// <param name="data">The layer's row storage.</param>
        /// <param name="arity">The layer's arity.</param>
        /// <param name="rowCount">The layer's physical row count.</param>
        /// <param name="excluded">Another sealed set whose rows are not counted again, or <c>null</c>.</param>
        /// <returns>The number of live rows counted.</returns>
        internal int CountLive(uint[] data, int arity, int rowCount, HiddenSet? excluded)
        {
            Debug.Assert(IsSealed);
            var live = 0;
            if (bits is { } b)
            {
                for (var word = 0; word < b.Length; word++)
                {
                    var w = b[word];
                    while (w != 0)
                    {
                        var row = (word << 6) + BitOperations.TrailingZeroCount(w);
                        w &= w - 1;
                        live += IsCountable(data, arity, rowCount, row, excluded) ? 1 : 0;
                    }
                }
            }
            else
            {
                foreach (var row in sorted)
                    live += IsCountable(data, arity, rowCount, row, excluded) ? 1 : 0;
            }

            return live;
        }

        /// <summary>
        /// Gets whether a row is live in the layer and not held by <paramref name="excluded"/>.
        /// </summary>
        static bool IsCountable(uint[] data, int arity, int rowCount, int row, HiddenSet? excluded)
        {
            return row < rowCount && data[row * arity] != Value.StaleRep && (excluded is null || excluded.SealedContains(row) == false);
        }

        /// <summary>
        /// Probes the sealed part.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool SealedContains(int row)
        {
            if (bits is { } b)
                return (uint)(row >> 6) < (uint)b.Length && row >= 0 && (b[row >> 6] & (1UL << row)) != 0;

            return sorted.AsSpan().BinarySearch(row) >= 0;
        }

        /// <summary>
        /// Gets whether a set of <paramref name="count"/> rows over a layer of <paramref name="layerRowCount"/> rows is
        /// smaller as a bitset.
        /// </summary>
        static bool ShouldBeBitset(int count, int layerRowCount) => count >= MinBitsetCount && count > layerRowCount / BitsetDivisor;

        /// <summary>
        /// Gets the number of 64-bit words that hold <paramref name="rows"/> bits.
        /// </summary>
        static int WordsFor(int rows) => (rows + 63) >> 6;

        /// <summary>
        /// Merges two sorted, distinct arrays into a new exactly sized sorted, distinct array.
        /// </summary>
        static int[] MergeSorted(int[] left, int[] right)
        {
            if (right.Length == 0)
                return left;

            if (left.Length == 0)
                return right;

            var result = new int[left.Length + right.Length];
            int i = 0, j = 0, k = 0;
            while (i < left.Length && j < right.Length)
            {
                var a = left[i];
                var b = right[j];
                if (a < b)
                {
                    result[k++] = a;
                    i++;
                }
                else if (b < a)
                {
                    result[k++] = b;
                    j++;
                }
                else
                {
                    result[k++] = a;
                    i++;
                    j++;
                }
            }

            while (i < left.Length)
                result[k++] = left[i++];

            while (j < right.Length)
                result[k++] = right[j++];

            if (k != result.Length)
                Array.Resize(ref result, k);

            return result;
        }

    }

}
