using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="TableEntry"/> that points at one particular row: compaction's lookup of the entry to
    /// renumber, egglog's <c>get_entry_mut(row, .., |x| x == old)</c> (core-relations/src/table/mod.rs:1214-1217).
    /// Compares the hash and the row id; never reads the row buffer.
    /// </summary>
    internal readonly struct RowIdMatch : IEntryMatch<TableEntry>
    {

        readonly ulong hash;
        readonly int row;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="hash">The hash of the row's key.</param>
        /// <param name="row">The row id the entry must point at.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RowIdMatch(ulong hash, int row)
        {
            this.hash = hash;
            this.row = row;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in TableEntry entry) => entry.Hash == hash && entry.Row == row;

    }

}
