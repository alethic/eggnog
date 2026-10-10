namespace Eggnog.Storage
{

    /// <summary>
    /// What a column of a table holds.
    /// </summary>
    public enum ColumnKind
    {

        /// <summary>
        /// An e-class <see cref="Storage.Id"/>. Rewritten to its canonical identifier whenever classes merge,
        /// which is what makes congruence closure work.
        /// </summary>
        Id,

        /// <summary>
        /// An opaque 64-bit value compared by its bits. The caller decides what it means; anything larger than
        /// 64 bits is interned by the caller and stored as its handle.
        /// </summary>
        Value,

    }

}
