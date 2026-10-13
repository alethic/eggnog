using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="TableEntry"/> of a two-cell key passed by value: the form behind
    /// <see cref="SortedWritesTable.FindRow(uint, uint)"/>.
    /// </summary>
    internal readonly struct Key2Match : IEntryMatch<TableEntry>
    {

        readonly uint[] rows;
        readonly int arity;
        readonly ulong hash;
        readonly uint k0;
        readonly uint k1;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rows">The row storage the index's entries point into.</param>
        /// <param name="arity">The cells per row.</param>
        /// <param name="hash">The key's hash.</param>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Key2Match(uint[] rows, int arity, ulong hash, uint k0, uint k1)
        {
            this.rows = rows;
            this.arity = arity;
            this.hash = hash;
            this.k0 = k0;
            this.k1 = k1;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in TableEntry entry)
        {
            if (entry.Hash != hash)
                return false;

            Debug.Assert(entry.Row >= 0 && ((long)entry.Row + 1) * arity <= rows.Length);
            ref var row = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(rows), entry.Row * arity);
            return row == k0 && Unsafe.Add(ref row, 1) == k1;
        }

    }

}
