using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A slot of a <see cref="TupleIndex"/> shard's hash table: egglog's <c>TableEntry&lt;BufferedSubset&gt;</c>
    /// (core-relations/src/hash_index/mod.rs:33-39). It holds the key's full hash, the row of the shard's key buffer
    /// where the key's cells are stored (once per distinct key, out of line), and the key's rows.
    /// </summary>
    /// <remarks>
    /// The stored hash rejects other keys before their cells are compared and lets the table grow without rehashing
    /// keys. Immutable: appending a row replaces the entry through a reference (<see cref="WithSubset"/>).
    /// </remarks>
    internal readonly struct TupleIndexEntry
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="hash">The key's hash (<see cref="TupleIndex.HashKey(System.ReadOnlySpan{uint})"/>).</param>
        /// <param name="keyRow">The row of the shard's key buffer holding the key.</param>
        /// <param name="subset">The key's rows.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TupleIndexEntry(ulong hash, int keyRow, BufferedSubset subset)
        {
            Hash = hash;
            KeyRow = keyRow;
            Subset = subset;
        }

        /// <summary>
        /// Gets the key's hash.
        /// </summary>
        public ulong Hash { get; }

        /// <summary>
        /// Gets the row of the shard's key buffer holding the key's cells.
        /// </summary>
        public int KeyRow { get; }

        /// <summary>
        /// Gets the key's rows.
        /// </summary>
        public BufferedSubset Subset { get; }

        /// <summary>
        /// Returns this entry with its rows replaced.
        /// </summary>
        /// <param name="subset">The new rows.</param>
        /// <returns>The entry.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TupleIndexEntry WithSubset(BufferedSubset subset) => new(Hash, KeyRow, subset);

    }

}
