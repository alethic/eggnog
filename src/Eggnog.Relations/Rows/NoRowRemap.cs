using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IRowRemap"/> that ignores the renumbering, for compactions nobody indexes.
    /// </summary>
    public readonly struct NoRowRemap : IRowRemap
    {

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remap(ReadOnlySpan<uint> row, int oldRow, int newRow)
        {

        }

    }

}
