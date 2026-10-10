namespace Eggnog.Relations
{

    /// <summary>
    /// Decides whether a stored entry is the one a lookup in a <see cref="RawHashTable{TEntry}"/> is looking for: the
    /// <c>eq</c> closure of hashbrown's <c>HashTable::find</c>.
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass the struct to the table's generic methods, so the JIT specializes the probe
    /// loop and inlines the comparison, the way Rust monomorphizes the closure. The table only calls it on full slots
    /// whose 7-bit tag equals the probed hash's tag, so a match should compare the cheap discriminating fields (a
    /// stored full hash, say) before the expensive ones (a key in a row buffer).
    /// </remarks>
    /// <typeparam name="TEntry">The entry type.</typeparam>
    internal interface IEntryMatch<TEntry>
        where TEntry : struct
    {

        /// <summary>
        /// Gets whether <paramref name="entry"/> matches. Must not modify the table.
        /// </summary>
        /// <param name="entry">A full slot's entry.</param>
        /// <returns><c>true</c> to stop the probe at this entry.</returns>
        bool Matches(in TEntry entry);

    }

}
