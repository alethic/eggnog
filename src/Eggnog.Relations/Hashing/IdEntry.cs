namespace Eggnog.Relations
{

    /// <summary>
    /// An id and a 32-bit payload, the entry of the id-keyed hash tables of colored contexts (colored-contexts spec
    /// §5.2): a union-find overlay's member-to-class map, a displaced journal's id-to-row map and a flattened prefix's
    /// id-to-canonical map.
    /// </summary>
    /// <remarks>
    /// Eight bytes, no stored hash: the hash is recomputed from <see cref="Id"/> with one multiply and a rotate
    /// (<see cref="IdEntryHasher"/>), which is cheaper than the memory a stored hash would cost per entry.
    /// </remarks>
    /// <param name="Id">The key.</param>
    /// <param name="Payload">The value: a class slot, a row, or a canonical id.</param>
    internal readonly record struct IdEntry(uint Id, uint Payload);

}
