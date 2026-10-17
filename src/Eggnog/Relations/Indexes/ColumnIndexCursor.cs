using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// An external cursor over the entries of a <see cref="ColumnIndex"/>, in <see cref="ColumnIndex.ForEach{TVisitor}"/>
    /// order: an external form of egglog's closure-based <c>for_each</c>, for a caller that drives the iteration itself
    /// and works between entries (<see cref="ProberCursor"/>).
    /// </summary>
    /// <remarks>
    /// <c>default</c> is positioned before the first entry. The index must not change while the cursor is used.
    /// </remarks>
    internal struct ColumnIndexCursor
    {

        int shard;
        int slot;
        uint key;
        SubsetRef rows;

        /// <summary>
        /// Gets the current value.
        /// </summary>
        public readonly uint Key => key;

        /// <summary>
        /// Gets the rows of the current value.
        /// </summary>
        public readonly SubsetRef Rows => rows;

        /// <summary>
        /// Advances to the next entry of <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The index; the same one on every call.</param>
        /// <returns><c>false</c> once every entry has been visited.</returns>
        public bool MoveNext(ColumnIndex index)
        {
            ArgumentNullException.ThrowIfNull(index);

            while (shard < index.ShardCount)
            {
                var found = index.NextEntry(shard, slot, out key, out rows);
                if (found >= 0)
                {
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
