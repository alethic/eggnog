namespace Eggnog.Relations
{

    /// <summary>
    /// Recomputes the 64-bit hash of a stored entry when a <see cref="RawHashTable{TEntry}"/> grows or rehashes in
    /// place: the <c>hasher</c> closure of hashbrown's <c>HashTable::insert_unique</c>, <c>entry</c> and
    /// <c>reserve</c>.
    /// </summary>
    /// <remarks>
    /// Implement it on a struct so the JIT specializes the rehash loop. Entries that keep their full hash (egglog's
    /// table and index entries do) return the stored field; others recompute it from their key, as egglog's
    /// <c>InternTable</c> does. It must return the hash the entry was inserted with, and it must not throw or modify
    /// the table: it runs while the table is being rebuilt.
    /// </remarks>
    /// <typeparam name="TEntry">The entry type.</typeparam>
    internal interface IEntryHasher<TEntry>
        where TEntry : struct
    {

        /// <summary>
        /// Gets the hash <paramref name="entry"/> was inserted with.
        /// </summary>
        /// <param name="entry">A stored entry.</param>
        /// <returns>The 64-bit hash.</returns>
        ulong Hash(in TEntry entry);

    }

}
