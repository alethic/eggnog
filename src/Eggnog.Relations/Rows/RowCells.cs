using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Cell-level helpers shared by the row buffers.
    /// </summary>
    internal static class RowCells
    {

        /// <summary>
        /// Copies <paramref name="length"/> cells between non-overlapping locations. Short rows, the common case,
        /// are copied by a plain loop, which beats a call into <c>memmove</c>; longer ones use the vectorized copy.
        /// The caller has checked both ranges.
        /// </summary>
        /// <param name="source">The first source cell.</param>
        /// <param name="destination">The first destination cell.</param>
        /// <param name="length">The number of cells.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Copy(ref uint source, ref uint destination, int length)
        {
            if (length <= 8)
            {
                for (var i = 0; i < length; i++)
                    Unsafe.Add(ref destination, i) = Unsafe.Add(ref source, i);
            }
            else
            {
                MemoryMarshal.CreateReadOnlySpan(ref source, length).CopyTo(MemoryMarshal.CreateSpan(ref destination, length));
            }
        }

    }

}
