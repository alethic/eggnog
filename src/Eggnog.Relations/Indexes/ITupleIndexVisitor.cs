using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Receives the entries of a <see cref="TupleIndex"/>: the closure egglog passes to <c>TupleIndex::for_each</c>
    /// (core-relations/src/hash_index/mod.rs:627-635).
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass it by reference to <see cref="TupleIndex.ForEach{TVisitor}(ref TVisitor)"/>,
    /// so the JIT specializes the walk and inlines <see cref="Visit"/>.
    /// </remarks>
    internal interface ITupleIndexVisitor
    {

        /// <summary>
        /// Visits one key and its rows.
        /// </summary>
        /// <param name="key">The key's cells; must not be kept.</param>
        /// <param name="rows">The rows listed under it; nonempty, valid until the index is next modified.</param>
        void Visit(ReadOnlySpan<uint> key, SubsetRef rows);

    }

}
