using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class SortedWritesTable
    {

        /// <summary>
        /// Gets the bytes of the row buffer's storage: its capacity, of which <see cref="RowBuffer.Count"/> rows are in
        /// use (live rows and tombstones). O(1).
        /// </summary>
        internal long RowStorageBytes => (long)data.Data.Length * sizeof(uint);

        /// <summary>
        /// Gets the bytes of the scratch buffer's storage: empty until a parallel compaction, which swaps it with the row
        /// buffer, so it keeps the storage the rows had before the last parallel compaction. O(1).
        /// </summary>
        internal long ScratchStorageBytes => (long)scratch.Data.Length * sizeof(uint);

        /// <summary>
        /// Gets the bytes of the key index's storage: every shard's entry slots and control bytes. O(shards).
        /// </summary>
        internal long KeyIndexBytes
        {
            get
            {
                long bytes = 0;
                for (var shard = 0; shard < hash.ShardCount; shard++)
                {
                    var table = hash.GetShard(shard);
                    bytes += (long)table.RawEntries.Length * Unsafe.SizeOf<TableEntry>() + table.Control.Length;
                }

                return bytes;
            }
        }

    }

}
