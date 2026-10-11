using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One entry of an <see cref="IndexCatalog{TIndex}"/>: the ordered key columns and the lazily refreshed index over
    /// them. egglog's <c>(K, Arc&lt;ResettableOnceLock&lt;Index&lt;TI&gt;&gt;&gt;)</c> pair.
    /// </summary>
    /// <typeparam name="TIndex">The index type.</typeparam>
    internal readonly struct IndexCatalogEntry<TIndex>
        where TIndex : class, IIndexBase<TIndex>
    {

        readonly ColumnId[] key;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="key">The key columns, owned by the entry.</param>
        /// <param name="index">The index cell.</param>
        public IndexCatalogEntry(ColumnId[] key, ResettableOnceLock<Index<TIndex>> index)
        {
            this.key = key;
            Index = index;
        }

        /// <summary>
        /// Gets the key columns, in key order.
        /// </summary>
        public ReadOnlySpan<ColumnId> Key => key;

        /// <summary>
        /// Gets the index cell: refreshed at most once per epoch, re-armed by <see cref="IndexCatalog{TIndex}.ResetAll"/>.
        /// </summary>
        public ResettableOnceLock<Index<TIndex>> Index { get; }

    }

}
