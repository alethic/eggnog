namespace Eggnog.Relations
{

    /// <summary>
    /// Decides which entries <see cref="RawHashTable{TEntry}.Retain{TPredicate}(ref TPredicate)"/> keeps: the closure of
    /// hashbrown's <c>HashTable::retain</c>.
    /// </summary>
    /// <remarks>
    /// Implement it on a struct so the JIT specializes the scan. The predicate may update the entry in place (the
    /// entry's hash must not change) but must not otherwise modify the table.
    /// </remarks>
    /// <typeparam name="TEntry">The entry type.</typeparam>
    internal interface IEntryPredicate<TEntry>
        where TEntry : struct
    {

        /// <summary>
        /// Gets whether <paramref name="entry"/> stays in the table.
        /// </summary>
        /// <param name="entry">A full slot's entry, which may be updated in place.</param>
        /// <returns><c>true</c> to keep the entry, <c>false</c> to remove it.</returns>
        bool Keep(ref TEntry entry);

    }

}
