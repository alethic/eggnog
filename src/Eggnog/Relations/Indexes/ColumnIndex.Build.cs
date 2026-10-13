using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class ColumnIndex
    {

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// egglog's sort-based <c>ColumnIndex::rebuild_full</c> (core-relations/src/hash_index/mod.rs:334-382): collect
        /// every (value, row) pair of the live rows, sort them, and give each value its whole subset in one step, dense
        /// when its rows are contiguous and otherwise one exactly sized arena run, so no subset is ever regrown.
        /// </para>
        /// <para>
        /// egglog collects the pairs column by column and sorts by value only
        /// (core-relations/src/hash_index/mod.rs:341-382), so with several columns a value's rows are the first
        /// column's rows followed by the second's, unsorted, and its adjacent <c>dedup</c> misses repeats. Here the
        /// pairs are packed as <c>value &lt;&lt; 32 | row</c> and come out sorted by (value, row)
        /// (<see cref="ColumnPairSink"/>, <see cref="PairRadixSort"/>), each row once per distinct value. The pair
        /// array is sized from the subset, not, as egglog does, from the whole table (which over-allocates when a large
        /// table's small subset is grouped).
        /// </para>
        /// <para>
        /// Cost: one scan, O(pairs × passes) to sort (at most four passes), two linear walks over the groups (one to size
        /// each shard exactly, one to fill it), and a pooled scratch of two words per pair.
        /// </para>
        /// </remarks>
        public void RebuildFull(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset)
        {
            var projection = CheckColumns(table, columns);
            Debug.Assert(Count == 0, "rebuild_full requires an empty index");
            if (RentPairs(subset, projection) is not { } pairs)
                return;

            var sink = new ColumnPairSink(pairs, projection);
            table.ScanGeneric(subset, ref sink);
            GroupPairs(pairs, sink.Count);
        }

        /// <summary>
        /// Builds the index from scratch from the rows of <paramref name="subset"/> a view sees (live and not in
        /// <paramref name="hidden"/>): the visibility-aware
        /// <see cref="RebuildFull(Table, ReadOnlySpan{ColumnId}, SubsetRef)"/> of per-subset builds, the same
        /// sort-based grouping over a hidden-aware scan. The index must be empty. With <see cref="HiddenRows.None"/> it
        /// is the plain build.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="columns">The key columns.</param>
        /// <param name="subset">The rows to index.</param>
        /// <param name="hidden">The rows the view hides; sealed.</param>
        public void RebuildFull(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset, HiddenRows hidden)
        {
            if (hidden.IsNone)
            {
                RebuildFull(table, columns, subset);
                return;
            }

            var projection = CheckColumns(table, columns);
            Debug.Assert(Count == 0, "rebuild_full requires an empty index");
            if (RentPairs(subset, projection) is not { } pairs)
                return;

            var sink = new ColumnPairSink(pairs, projection);
            table.ScanGeneric(subset, hidden, ref sink);
            GroupPairs(pairs, sink.Count);
        }

        /// <summary>
        /// Rents the pair scratch of a sort-based build: room for every (value, row) pair of <paramref name="subset"/>
        /// projected onto <paramref name="projection"/>, or <c>null</c> when there are none.
        /// </summary>
        /// <exception cref="InvalidOperationException">The pairs would not fit in one array.</exception>
        static ulong[]? RentPairs(SubsetRef subset, int[] projection)
        {
            var capacity = (long)subset.Size * projection.Length;
            if (capacity == 0)
                return null;

            if (capacity > Array.MaxLength)
                throw new InvalidOperationException($"Indexing {subset.Size} rows by {projection.Length} columns needs more than {Array.MaxLength} pairs.");

            return PooledArray.Allocate<ulong>((int)capacity);
        }

        /// <summary>
        /// Sorts the first <paramref name="count"/> scanned pairs, inserts their groups, and releases the scratch.
        /// </summary>
        void GroupPairs(ulong[] pairs, int count)
        {
            PairRadixSort.SortByValue(pairs, count);
            InsertGroups(pairs, count);
            PooledArray.Release(pairs);
        }

        /// <summary>
        /// Inserts each group of equal values of <c>pairs[0..count)</c>, which are sorted by (value, row) without
        /// duplicates, as a new key: the grouping loop of egglog's <c>rebuild_full</c>. A group is dense when its last row
        /// minus its first is one less than its size.
        /// </summary>
        /// <param name="pairs">The packed pairs.</param>
        /// <param name="count">The number of pairs.</param>
        void InsertGroups(ulong[] pairs, int count)
        {
            AssertStrictlySorted(pairs, count);
            var shardCount = shards.Length;
            Span<int> keys = shardCount <= 256 ? stackalloc int[shardCount] : new int[shardCount];
            Span<long> slots = shardCount <= 256 ? stackalloc long[shardCount] : new long[shardCount];
            keys.Clear();
            slots.Clear();

            // First walk: size every shard's map, entries and arena exactly.
            for (var i = 0; i < count;)
            {
                var start = i;
                var value = (uint)(pairs[i] >> 32);
                i = GroupEnd(pairs, i, count, value);
                var shard = shardData.ShardOf(FxHasher.Hash(value));
                keys[shard]++;
                if (IsContiguous(pairs, start, i) == false)
                    slots[shard] += SubsetBuffer.BlockSize(i - start);
            }

            for (var s = 0; s < shardCount; s++)
                shards[s].Reserve(keys[s], slots[s]);

            // Second walk: insert.
            for (var i = 0; i < count;)
            {
                var start = i;
                var value = (uint)(pairs[i] >> 32);
                i = GroupEnd(pairs, i, count, value);
                var hash = FxHasher.Hash(value);
                ref var shard = ref shards[shardData.ShardOf(hash)];
                BufferedSubset subset;
                if (IsContiguous(pairs, start, i))
                {
                    subset = BufferedSubset.Dense((int)(uint)pairs[start], (int)(uint)pairs[i - 1] + 1);
                }
                else
                {
                    var length = i - start;
                    var at = shard.AllocateRun(length);
                    var run = shard.Arena.AsSpan(at, length);
                    for (var j = 0; j < run.Length; j++)
                        run[j] = (int)(uint)pairs[start + j];

                    subset = BufferedSubset.Sparse(at, at + length);
                }

                shard.InsertNew(value, hash, subset);
            }
        }

        /// <summary>
        /// Gets one past the last position, from <paramref name="i"/>, of the pairs whose value is <paramref name="value"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int GroupEnd(ulong[] pairs, int i, int count, uint value)
        {
            i++;
            while (i < count && (uint)(pairs[i] >> 32) == value)
                i++;

            return i;
        }

        /// <summary>
        /// Gets whether the rows of the group <c>pairs[start..end)</c> are consecutive row ids (egglog's
        /// <c>last - first == count - 1</c>; the group is sorted without duplicates).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsContiguous(ulong[] pairs, int start, int end) => (int)(uint)pairs[end - 1] - (int)(uint)pairs[start] == end - start - 1;

        /// <summary>
        /// Debug builds: checks that the pairs are sorted by (value, row) without duplicates, which grouping requires.
        /// </summary>
        [Conditional("DEBUG")]
        static void AssertStrictlySorted(ulong[] pairs, int count)
        {
            for (var i = 1; i < count; i++)
                Debug.Assert(pairs[i - 1] < pairs[i], "index pairs are not sorted by (value, row) without duplicates");
        }

    }

}
