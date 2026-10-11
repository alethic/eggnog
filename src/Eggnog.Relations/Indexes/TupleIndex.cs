using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A hash index from a tuple of column values to the rows holding it: egglog's <c>TupleIndex</c>
    /// (core-relations/src/hash_index/mod.rs:535-736). The join probes it with the values bound to several of an atom's
    /// columns at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: shards selected by the key's hash (<see cref="HashKey(ReadOnlySpan{uint})"/>, egglog's <c>hash_key</c>:
    /// Fx over the length, then each cell), each a SwissTable of <see cref="TupleIndexEntry"/>s, a buffer storing every
    /// distinct key once, and an arena of sparse subsets (<see cref="TupleIndexShard"/>). Keys of one, two and three
    /// cells are hashed and compared by unrolled code with no array; longer keys compare as vectors.
    /// </para>
    /// <para>
    /// Contents: the rows under a key are sorted and nonempty; rows are added in ascending order only. Rows deleted
    /// after they were indexed stay listed until the table's next generation, so readers filter tombstones.
    /// </para>
    /// <para>
    /// Threading: any number of readers while nobody writes; one writer at a time (it uses a scratch key array for long
    /// keys). A parallel build splits work so that each shard has one writer.
    /// </para>
    /// </remarks>
    internal sealed partial class TupleIndex : IIndexBase<TupleIndex>, IDisposable
    {

        readonly ParallelHeuristics parallelism;
        readonly ShardData shardData;
        readonly TupleIndexShard[] shards;
        readonly int arity;
        readonly uint[] scratchKey;

        /// <summary>
        /// Initializes an empty index for keys of <paramref name="arity"/> cells, with the shard count of
        /// <paramref name="parallelism"/>: egglog's <c>TupleIndex::new</c>. Rents and allocates no per-shard storage
        /// until a shard receives its first key.
        /// </summary>
        /// <param name="arity">The key arity; at least one (egglog's <c>RowBuffer::new</c> asserts it).</param>
        /// <param name="parallelism">The parallelism settings.</param>
        public TupleIndex(int arity, ParallelHeuristics parallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(arity, 1);
            ArgumentNullException.ThrowIfNull(parallelism);

            this.arity = arity;
            this.parallelism = parallelism;
            shardData = parallelism.ShardData;
            shards = new TupleIndexShard[shardData.Count];
            for (var i = 0; i < shards.Length; i++)
                shards[i] = TupleIndexShard.Create(arity);

            scratchKey = new uint[arity];
        }

        /// <summary>
        /// Initializes an index over existing shards, for <see cref="Clone"/>.
        /// </summary>
        TupleIndex(int arity, ParallelHeuristics parallelism, TupleIndexShard[] shards)
        {
            this.arity = arity;
            this.parallelism = parallelism;
            shardData = parallelism.ShardData;
            this.shards = shards;
            scratchKey = new uint[arity];
        }

        /// <inheritdoc />
        public static TupleIndex Create(int keyArity, ParallelHeuristics parallelism) => new(keyArity, parallelism);

        /// <inheritdoc />
        public ParallelHeuristics Parallelism => parallelism;

        /// <summary>
        /// Gets the key arity.
        /// </summary>
        public int Arity => arity;

        /// <summary>
        /// Gets the shard layout.
        /// </summary>
        public ShardData ShardData => shardData;

        /// <summary>
        /// Gets the number of shards.
        /// </summary>
        public int ShardCount => shards.Length;

        /// <summary>
        /// Gets the number of distinct keys. egglog's <c>len</c>. O(shards).
        /// </summary>
        public int Count
        {
            get
            {
                var total = 0;
                foreach (ref readonly var shard in shards.AsSpan())
                    total += shard.Count;

                return total;
            }
        }

        /// <summary>
        /// Hashes a key: egglog's <c>hash_key</c> (core-relations/src/hash_index/mod.rs:732-736), the Fx hash of the
        /// slice, which writes its length and then each cell. O(key length).
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The 64-bit hash.</returns>
        public static ulong HashKey(ReadOnlySpan<uint> key)
        {
            var hash = (ulong)key.Length * FxHasher.Seed;
            foreach (var cell in key)
                hash = (hash + cell) * FxHasher.Seed;

            return BitOperations.RotateLeft(hash, 26);
        }

        /// <summary>
        /// Hashes a one-cell key; equal to <see cref="HashKey(ReadOnlySpan{uint})"/> of it.
        /// </summary>
        /// <param name="k0">The cell.</param>
        /// <returns>The hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong HashKey(uint k0) => BitOperations.RotateLeft((FxHasher.Seed + k0) * FxHasher.Seed, 26);

        /// <summary>
        /// Hashes a two-cell key; equal to <see cref="HashKey(ReadOnlySpan{uint})"/> of it.
        /// </summary>
        /// <param name="k0">The first cell.</param>
        /// <param name="k1">The second cell.</param>
        /// <returns>The hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong HashKey(uint k0, uint k1) => BitOperations.RotateLeft(((unchecked(2 * FxHasher.Seed) + k0) * FxHasher.Seed + k1) * FxHasher.Seed, 26);

        /// <summary>
        /// Hashes a three-cell key; equal to <see cref="HashKey(ReadOnlySpan{uint})"/> of it.
        /// </summary>
        /// <param name="k0">The first cell.</param>
        /// <param name="k1">The second cell.</param>
        /// <param name="k2">The third cell.</param>
        /// <returns>The hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong HashKey(uint k0, uint k1, uint k2) => BitOperations.RotateLeft((((unchecked(3 * FxHasher.Seed) + k0) * FxHasher.Seed + k1) * FxHasher.Seed + k2) * FxHasher.Seed, 26);

        /// <summary>
        /// Looks up the rows of a one-cell key. For indexes of arity one; no array needed.
        /// </summary>
        /// <param name="k0">The key.</param>
        /// <param name="rows">The rows, sorted and nonempty, valid until the index is next modified.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetSubset(uint k0, out SubsetRef rows)
        {
            CheckArity(1);
            var hash = HashKey(k0);
            ref readonly var shard = ref shards[shardData.ShardOf(hash)];
            var match = new TupleKey1Match(shard.KeyData, hash, k0);
            return shard.TryGetSubset(hash, ref match, out rows);
        }

        /// <summary>
        /// Looks up the rows of a two-cell key. For indexes of arity two; no array needed.
        /// </summary>
        /// <param name="k0">The first cell.</param>
        /// <param name="k1">The second cell.</param>
        /// <param name="rows">The rows, sorted and nonempty, valid until the index is next modified.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetSubset(uint k0, uint k1, out SubsetRef rows)
        {
            CheckArity(2);
            var hash = HashKey(k0, k1);
            ref readonly var shard = ref shards[shardData.ShardOf(hash)];
            var match = new TupleKey2Match(shard.KeyData, hash, k0, k1);
            return shard.TryGetSubset(hash, ref match, out rows);
        }

        /// <summary>
        /// Looks up the rows of a three-cell key. For indexes of arity three; no array needed.
        /// </summary>
        /// <param name="k0">The first cell.</param>
        /// <param name="k1">The second cell.</param>
        /// <param name="k2">The third cell.</param>
        /// <param name="rows">The rows, sorted and nonempty, valid until the index is next modified.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetSubset(uint k0, uint k1, uint k2, out SubsetRef rows)
        {
            CheckArity(3);
            var hash = HashKey(k0, k1, k2);
            ref readonly var shard = ref shards[shardData.ShardOf(hash)];
            var match = new TupleKey3Match(shard.KeyData, hash, k0, k1, k2);
            return shard.TryGetSubset(hash, ref match, out rows);
        }

        /// <summary>
        /// Looks up the rows of the key held at <paramref name="key"/>[<paramref name="offset"/>]: the array form for
        /// compiled plans. egglog's <c>get_subset</c>. O(1) expected, no allocation.
        /// </summary>
        /// <param name="key">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <param name="rows">The rows, sorted and nonempty, valid until the index is next modified.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        public bool TryGetSubset(uint[] key, int offset, out SubsetRef rows)
        {
            ArgumentNullException.ThrowIfNull(key);
            if ((uint)offset > (uint)key.Length || key.Length - offset < arity)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, $"The array does not hold a key of {arity} cells at the offset.");

            var hash = HashKey(new ReadOnlySpan<uint>(key, offset, arity));
            ref readonly var shard = ref shards[shardData.ShardOf(hash)];
            var match = new TupleKeyMatch(shard.KeyData, arity, hash, key, offset);
            return shard.TryGetSubset(hash, ref match, out rows);
        }

        /// <summary>
        /// Looks up the rows of <paramref name="key"/>. egglog's <c>get_subset</c>. O(1) expected; keys of more than three
        /// cells are copied into a rented array for the compare.
        /// </summary>
        /// <param name="key">The key; exactly <see cref="Arity"/> cells.</param>
        /// <param name="rows">The rows, sorted and nonempty, valid until the index is next modified.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        public bool TryGetSubset(ReadOnlySpan<uint> key, out SubsetRef rows)
        {
            if (key.Length != arity)
                ThrowKeyLength(key.Length);

            switch (arity)
            {
                case 1:
                    return TryGetSubset(key[0], out rows);
                case 2:
                    return TryGetSubset(key[0], key[1], out rows);
                case 3:
                    return TryGetSubset(key[0], key[1], key[2], out rows);
                default:
                    var copy = ArrayPool<uint>.Shared.Rent(arity);
                    key.CopyTo(copy);
                    var found = TryGetSubset(copy, 0, out rows);
                    ArrayPool<uint>.Shared.Return(copy);
                    return found;
            }
        }

        /// <summary>
        /// Lists <paramref name="row"/> under <paramref name="key"/>, storing the key if it is new; <paramref name="row"/>
        /// must be at least every row already listed under it. egglog's <c>add_row</c>. Amortized O(1).
        /// </summary>
        /// <param name="key">The key; exactly <see cref="Arity"/> cells.</param>
        /// <param name="row">The row id.</param>
        public void AddRow(ReadOnlySpan<uint> key, int row)
        {
            if (key.Length != arity)
                ThrowKeyLength(key.Length);

            switch (arity)
            {
                case 1:
                {
                    var k0 = key[0];
                    var hash = HashKey(k0);
                    ref var shard = ref shards[shardData.ShardOf(hash)];
                    var match = new TupleKey1Match(shard.KeyData, hash, k0);
                    shard.AddRow(hash, ref match, key, row);
                    return;
                }
                case 2:
                {
                    var k0 = key[0];
                    var k1 = key[1];
                    var hash = HashKey(k0, k1);
                    ref var shard = ref shards[shardData.ShardOf(hash)];
                    var match = new TupleKey2Match(shard.KeyData, hash, k0, k1);
                    shard.AddRow(hash, ref match, key, row);
                    return;
                }
                case 3:
                {
                    var k0 = key[0];
                    var k1 = key[1];
                    var k2 = key[2];
                    var hash = HashKey(k0, k1, k2);
                    ref var shard = ref shards[shardData.ShardOf(hash)];
                    var match = new TupleKey3Match(shard.KeyData, hash, k0, k1, k2);
                    shard.AddRow(hash, ref match, key, row);
                    return;
                }
                default:
                {
                    var hash = HashKey(key);
                    key.CopyTo(scratchKey);
                    shards[shardData.ShardOf(hash)].AddRow(hash, scratchKey, 0, row);
                    return;
                }
            }
        }

        /// <summary>
        /// Adds every row of a tagged buffer under its cells: egglog's <c>merge_rows</c>.
        /// </summary>
        /// <param name="buffer">Keys tagged with their row ids, in ascending row order; its arity is <see cref="Arity"/>.</param>
        public void MergeRows(TaggedRowBuffer buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            for (var i = 0; i < buffer.Count; i++)
            {
                var key = buffer.GetRow(i, out var rowId);
                AddRow(key, rowId);
            }
        }

        /// <summary>
        /// Visits every key and its rows: shards in order, keys in hash-table slot order within a shard (egglog follows
        /// hashbrown's order). egglog's <c>for_each</c>. O(keys + slots), no allocation.
        /// </summary>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="visitor">The visitor.</param>
        public void ForEach<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ITupleIndexVisitor
        {
            foreach (ref readonly var shard in shards.AsSpan())
            {
                var keys = shard.KeyData;
                var arena = shard.Arena;
                foreach (ref var entry in shard.Table)
                    visitor.Visit(new ReadOnlySpan<uint>(keys, entry.KeyRow * arity, arity), entry.Subset.AsRef(arena));
            }
        }

        /// <summary>
        /// Gets the first entry of shard <paramref name="shard"/> at or after hash slot <paramref name="slot"/>, for
        /// external iteration (<see cref="TupleIndexCursor"/>).
        /// </summary>
        /// <param name="shard">The shard, in <c>[0, ShardCount)</c>.</param>
        /// <param name="slot">The slot to start from.</param>
        /// <param name="keyOffset">The offset of the key's cells in <paramref name="keyData"/>.</param>
        /// <param name="keyData">The array holding the key.</param>
        /// <param name="rows">The key's rows.</param>
        /// <returns>The entry's slot, or -1 if the shard has no entry at or after <paramref name="slot"/>.</returns>
        public int NextEntry(int shard, int slot, out uint[] keyData, out int keyOffset, out SubsetRef rows)
        {
            ref readonly var s = ref shards[shard];
            var found = s.Table.NextFullSlot(slot);
            if (found < 0)
            {
                keyData = Array.Empty<uint>();
                keyOffset = 0;
                rows = default;
                return -1;
            }

            var entry = s.Table.GetEntry(found);
            keyData = s.KeyData;
            keyOffset = entry.KeyRow * arity;
            rows = entry.Subset.AsRef(s.Arena);
            return found;
        }

        /// <summary>
        /// Removes every key, keeping the storage. egglog's <c>clear</c>.
        /// </summary>
        public void Clear()
        {
            for (var i = 0; i < shards.Length; i++)
                shards[i].Clear();
        }

        /// <inheritdoc />
        public void MergeSubset(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset)
        {
            CheckColumns(table, columns);
            if (subset.IsEmpty)
                return;

            var sink = new TupleIndexInsertSink(this);
            table.ScanProject(subset, columns, 0, int.MaxValue, ReadOnlySpan<Constraint>.Empty, ref sink);
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's default <c>IndexBase::rebuild_full</c> (core-relations/src/hash_index/mod.rs:172-188), which
        /// <c>TupleIndex</c> does not override: the row-at-a-time build.
        /// </remarks>
        public void RebuildFull(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset) => MergeSubset(table, columns, subset);

        /// <summary>
        /// Groups the live rows of <paramref name="subset"/> of <paramref name="table"/> by <paramref name="columns"/>:
        /// egglog's <c>group_by_key</c> (core-relations/src/table_spec.rs:414-440), which the join uses for multi-column
        /// probes of small subsets. Like <see cref="ColumnIndex.BuildForSubset"/>, the index has one shard, since it is
        /// never built in parallel.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="subset">The rows to group.</param>
        /// <param name="columns">The key columns, in key order; at least one (egglog cannot build a zero-arity index
        /// either).</param>
        /// <returns>The index, which the caller owns. Disposing it when done hands its storage back to the pools, so
        /// that the next build reuses it.</returns>
        public static TupleIndex BuildForSubset(Table table, SubsetRef subset, ReadOnlySpan<ColumnId> columns)
        {
            ArgumentNullException.ThrowIfNull(table);

            var index = new TupleIndex(columns.Length, ParallelHeuristics.Serial);
            index.MergeSubset(table, columns, subset);
            return index;
        }

        /// <inheritdoc />
        public TupleIndex Clone()
        {
            var copy = new TupleIndexShard[shards.Length];
            for (var i = 0; i < shards.Length; i++)
                copy[i] = shards[i].Clone();

            return new TupleIndex(arity, parallelism, copy);
        }

        /// <summary>
        /// Hands every shard's hash table, key storage and arena back to the engine's pools
        /// (<see cref="TupleIndexHashPolicy"/>, the shared array pool, <see cref="SubsetArenaPolicy"/>), as dropping
        /// egglog's index returns its pooled tables, key vectors and arenas, and empties the index. It stays usable and
        /// rents again on the next key; disposing twice is harmless. Subsets and key data taken from it must not be used
        /// afterwards. Optional: an index that is simply dropped is reclaimed by the garbage collector, and its storage
        /// is not reused. Allocates nothing; O(shards) plus clearing each table's control bytes.
        /// </summary>
        public void Dispose()
        {
            for (var i = 0; i < shards.Length; i++)
                shards[i].Release();
        }

        /// <summary>
        /// Checks that <paramref name="columns"/> are <see cref="Arity"/> columns of <paramref name="table"/>.
        /// </summary>
        void CheckColumns(Table table, ReadOnlySpan<ColumnId> columns)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (columns.Length != arity)
                throw new ArgumentException($"The index has keys of {arity} columns, not {columns.Length}.", nameof(columns));

            foreach (var column in columns)
            {
                if ((uint)column.Rep >= (uint)table.Arity)
                    throw new ArgumentOutOfRangeException(nameof(columns), column.Rep, $"The table has {table.Arity} columns.");
            }
        }

        /// <summary>
        /// Throws unless the index's arity is <paramref name="expected"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckArity(int expected)
        {
            if (arity != expected)
                ThrowKeyLength(expected);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowKeyLength(int length)
        {
            throw new ArgumentException($"The index has keys of {arity} cells, not {length}.", "key");
        }

    }

}
