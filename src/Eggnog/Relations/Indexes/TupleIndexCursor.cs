using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// An external cursor over the entries of a <see cref="TupleIndex"/>, in <see cref="TupleIndex.ForEach{TVisitor}"/>
    /// order: the iteration compiled plans use in place of egglog's closure-based <c>for_each</c> (map §5.3).
    /// </summary>
    /// <remarks>
    /// <c>default</c> is positioned before the first entry. The current key is exposed as an array and offset, which
    /// expression trees can read. The index must not change while the cursor is used.
    /// </remarks>
    internal struct TupleIndexCursor
    {

        int shard;
        int slot;
        uint[]? keyData;
        int keyOffset;
        int arity;
        SubsetRef rows;

        /// <summary>
        /// Gets the array holding the current key's cells.
        /// </summary>
        public readonly uint[] KeyData => keyData ?? Array.Empty<uint>();

        /// <summary>
        /// Gets the offset of the current key's first cell in <see cref="KeyData"/>.
        /// </summary>
        public readonly int KeyOffset => keyOffset;

        /// <summary>
        /// Gets the current key's cells.
        /// </summary>
        public readonly ReadOnlySpan<uint> Key => new(keyData, keyOffset, arity);

        /// <summary>
        /// Gets the rows of the current key.
        /// </summary>
        public readonly SubsetRef Rows => rows;

        /// <summary>
        /// Advances to the next entry of <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The index; the same one on every call.</param>
        /// <returns><c>false</c> once every entry has been visited.</returns>
        public bool MoveNext(TupleIndex index)
        {
            ArgumentNullException.ThrowIfNull(index);

            arity = index.Arity;
            while (shard < index.ShardCount)
            {
                var found = index.NextEntry(shard, slot, out var data, out keyOffset, out rows);
                if (found >= 0)
                {
                    keyData = data;
                    slot = found + 1;
                    return true;
                }

                shard++;
                slot = 0;
            }

            return false;
        }

    }

}
