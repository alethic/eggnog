namespace Eggnog.Relations
{

    /// <summary>
    /// A slot of a <see cref="Materialization"/>'s hash table: the full hash of a key and the index of its group, whose
    /// key cells live in the materialization's key store.
    /// </summary>
    /// <param name="Hash">The key's 64-bit hash.</param>
    /// <param name="Group">The group's index, in insertion order.</param>
    internal readonly record struct MaterializationEntry(ulong Hash, int Group);

}
