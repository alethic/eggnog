using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A slot of a <see cref="ColumnIndex"/> shard's hash table: a value and the rows listed under it. egglog's
    /// <c>IndexMap&lt;Value, BufferedSubset&gt;</c> entry (core-relations/src/hash_index/mod.rs:191-194), stored inline
    /// in the SwissTable.
    /// </summary>
    /// <remarks>
    /// Twelve bytes. Keeping the subset in the slot, rather than behind a position into a separate entries array as an
    /// <c>IndexMap</c> does, saves a dependent load (a cache miss on large indexes) on every probe and every append;
    /// iteration walks the table's slots instead of an insertion-ordered array. Immutable: appending a row replaces the
    /// entry through a reference.
    /// </remarks>
    internal readonly struct ColumnIndexEntry
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="key">The value.</param>
        /// <param name="subset">Its rows.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ColumnIndexEntry(uint key, BufferedSubset subset)
        {
            Key = key;
            Subset = subset;
        }

        /// <summary>
        /// Gets the value.
        /// </summary>
        public uint Key { get; }

        /// <summary>
        /// Gets the rows listed under the value.
        /// </summary>
        public BufferedSubset Subset { get; }

    }

}
