namespace Eggnog.Relations
{

    /// <summary>
    /// Receives the entries of a <see cref="ColumnIndex"/>: the closure egglog passes to <c>ColumnIndex::for_each</c>
    /// (core-relations/src/hash_index/mod.rs:251-259).
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass it by reference to <see cref="ColumnIndex.ForEach{TVisitor}(ref TVisitor)"/>,
    /// so the JIT specializes the walk and inlines <see cref="Visit"/>.
    /// </remarks>
    internal interface IColumnIndexVisitor
    {

        /// <summary>
        /// Visits one key and its rows.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="rows">The rows listed under it; nonempty, valid until the index is next modified.</param>
        void Visit(uint key, SubsetRef rows);

    }

}
