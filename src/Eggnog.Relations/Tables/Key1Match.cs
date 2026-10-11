using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="TableEntry"/> of a one-cell key passed by value: the form behind
    /// <see cref="SortedWritesTable.FindRow(uint)"/>, which compiled plans call without an array.
    /// </summary>
    internal readonly struct Key1Match : IEntryMatch<TableEntry>
    {

        readonly uint[] rows;
        readonly int arity;
        readonly ulong hash;
        readonly uint k0;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rows">The row storage the index's entries point into.</param>
        /// <param name="arity">The cells per row.</param>
        /// <param name="hash">The key's hash.</param>
        /// <param name="k0">The key.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Key1Match(uint[] rows, int arity, ulong hash, uint k0)
        {
            this.rows = rows;
            this.arity = arity;
            this.hash = hash;
            this.k0 = k0;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in TableEntry entry)
        {
            Debug.Assert(entry.Row >= 0 && ((long)entry.Row + 1) * arity <= rows.Length);
            return entry.Hash == hash && Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(rows), entry.Row * arity) == k0;
        }

    }

}
