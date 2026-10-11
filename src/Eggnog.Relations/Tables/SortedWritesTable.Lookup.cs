using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class SortedWritesTable
    {

        /// <summary>
        /// Scratch for keys of more than three cells passed as spans, which the key index can only compare from an
        /// array. One per thread; lookups are reads and run on many threads.
        /// </summary>
        [ThreadStatic]
        static uint[]? keyScratch;

        /// <summary>
        /// Finds the live row with the one-cell key <paramref name="k0"/>. For compiled plans: no span, no allocation,
        /// a non-virtual call on a sealed class. Expected O(1).
        /// </summary>
        /// <param name="k0">The key.</param>
        /// <returns>The row id, or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FindRow(uint k0)
        {
            if (keyCount != 1)
                ThrowKeyCount(1);

            var keyHash = FxHasher.Hash(k0);
            var match = new Key1Match(data.Data, Arity, keyHash, k0);
            var shard = hash.ShardFor(keyHash);
            var slot = shard.Find(keyHash, ref match);
            return slot < 0 ? -1 : shard.EntryAt(slot).Row;
        }

        /// <summary>
        /// Finds the live row with the two-cell key (<paramref name="k0"/>, <paramref name="k1"/>). Expected O(1).
        /// </summary>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        /// <returns>The row id, or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FindRow(uint k0, uint k1)
        {
            if (keyCount != 2)
                ThrowKeyCount(2);

            var keyHash = FxHasher.Hash(k0, k1);
            var match = new Key2Match(data.Data, Arity, keyHash, k0, k1);
            var shard = hash.ShardFor(keyHash);
            var slot = shard.Find(keyHash, ref match);
            return slot < 0 ? -1 : shard.EntryAt(slot).Row;
        }

        /// <summary>
        /// Finds the live row with the three-cell key (<paramref name="k0"/>, <paramref name="k1"/>,
        /// <paramref name="k2"/>). Expected O(1).
        /// </summary>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        /// <param name="k2">The third key cell.</param>
        /// <returns>The row id, or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FindRow(uint k0, uint k1, uint k2)
        {
            if (keyCount != 3)
                ThrowKeyCount(3);

            var keyHash = FxHasher.Hash(k0, k1, k2);
            var match = new Key3Match(data.Data, Arity, keyHash, k0, k1, k2);
            var shard = hash.ShardFor(keyHash);
            var slot = shard.Find(keyHash, ref match);
            return slot < 0 ? -1 : shard.EntryAt(slot).Row;
        }

        /// <summary>
        /// Finds the live row whose key is the <see cref="KeyCount"/> cells starting at
        /// <paramref name="key"/>[<paramref name="offset"/>]: the form for compiled plans with wide keys. Expected
        /// O(<see cref="KeyCount"/>).
        /// </summary>
        /// <param name="key">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <returns>The row id, or -1.</returns>
        public int FindRow(uint[] key, int offset)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (offset < 0 || key.Length - offset < keyCount)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "The array does not hold a whole key at the offset.");

            return FindRowCore(key, offset);
        }

        /// <summary>
        /// Finds the live row with key <paramref name="key"/>: egglog's <c>get_entry</c>
        /// (core-relations/src/table/mod.rs:1248-1261). Keys of up to three cells are compared from registers; wider
        /// ones are copied to a per-thread scratch array first. Expected O(<see cref="KeyCount"/>).
        /// </summary>
        /// <param name="key">The key; exactly <see cref="KeyCount"/> cells.</param>
        /// <returns>The row id, or -1.</returns>
        public int FindRow(ReadOnlySpan<uint> key)
        {
            if (key.Length != keyCount)
                throw new ArgumentException($"Expected a key of {keyCount} cells but got {key.Length}.", nameof(key));

            switch (keyCount)
            {
                case 0:
                    return FindRowCore(Array.Empty<uint>(), 0);
                case 1:
                    return FindRow(key[0]);
                case 2:
                    return FindRow(key[0], key[1]);
                case 3:
                    return FindRow(key[0], key[1], key[2]);
                default:
                    var buffer = keyScratch;
                    if (buffer is null || buffer.Length < keyCount)
                        keyScratch = buffer = new uint[Math.Max(keyCount, 8)];

                    key.CopyTo(buffer);
                    return FindRowCore(buffer, 0);
            }
        }

        /// <summary>
        /// Gets column <paramref name="column"/> of the live row with key <paramref name="k0"/>: egglog's
        /// <c>get_row_column</c> for compiled plans. Expected O(1).
        /// </summary>
        /// <param name="k0">The key.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetColumn(uint k0, int column, out uint value) => TryReadColumn(FindRow(k0), column, out value);

        /// <summary>
        /// Gets column <paramref name="column"/> of the live row with the two-cell key (<paramref name="k0"/>,
        /// <paramref name="k1"/>). Expected O(1).
        /// </summary>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetColumn(uint k0, uint k1, int column, out uint value) => TryReadColumn(FindRow(k0, k1), column, out value);

        /// <summary>
        /// Gets column <paramref name="column"/> of the live row with the three-cell key. Expected O(1).
        /// </summary>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        /// <param name="k2">The third key cell.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetColumn(uint k0, uint k1, uint k2, int column, out uint value) => TryReadColumn(FindRow(k0, k1, k2), column, out value);

        /// <summary>
        /// Gets column <paramref name="column"/> of the live row whose key starts at
        /// <paramref name="key"/>[<paramref name="offset"/>]. The form for compiled plans with wide keys.
        /// </summary>
        /// <param name="key">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        public bool TryGetColumn(uint[] key, int offset, int column, out uint value) => TryReadColumn(FindRow(key, offset), column, out value);

        /// <inheritdoc />
        /// <remarks>egglog's <c>get_row</c> (core-relations/src/table/mod.rs:537-544), copying into the caller's span.</remarks>
        public override bool TryGetRow(ReadOnlySpan<uint> key, Span<uint> destination, out int rowId)
        {
            if (destination.Length < Arity)
                throw new ArgumentException($"The destination needs room for {Arity} cells.", nameof(destination));

            rowId = FindRow(key);
            if (rowId < 0)
                return false;

            data.GetRow(rowId).CopyTo(destination);
            return true;
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>get_row_column</c> (core-relations/src/table/mod.rs:546-551).</remarks>
        public override bool TryGetRowColumn(ReadOnlySpan<uint> key, ColumnId column, out uint value)
        {
            return TryReadColumn(FindRow(key), column.Rep, out value);
        }

        /// <summary>
        /// Probes the index for the key starting at <paramref name="key"/>[<paramref name="offset"/>], which the caller
        /// has checked.
        /// </summary>
        int FindRowCore(uint[] key, int offset)
        {
            var keyHash = TableKeyHash.Hash(key, offset, keyCount);
            var match = new KeyMatch(data.Data, Arity, key, offset, keyCount, keyHash);
            var shard = hash.ShardFor(keyHash);
            var slot = shard.Find(keyHash, ref match);
            return slot < 0 ? -1 : shard.EntryAt(slot).Row;
        }

        /// <summary>
        /// Reads one cell of <paramref name="row"/>, or reports absence for -1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool TryReadColumn(int row, int column, out uint value)
        {
            if ((uint)column >= (uint)Arity)
                ThrowColumn(column);

            if (row < 0)
            {
                value = 0;
                return false;
            }

            value = data.Data[row * Arity + column];
            return true;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowKeyCount(int used)
        {
            throw new InvalidOperationException($"A lookup with {used} key cells on a table with {keyCount} key columns.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowColumn(int column)
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, $"The table has {Arity} columns.");
        }

    }

}
