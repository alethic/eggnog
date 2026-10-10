using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Writes ascending runs of identifiers, the "every id is its own parent" state of a union-find.
    /// </summary>
    /// <remarks>
    /// egglog initializes union-find slots one element at a time (<c>union-find/src/lib.rs:39-52</c>,
    /// <c>concurrent/buffer.rs:43-98</c>). Growth and reset are linear in the slots touched either way; this fills
    /// whole <see cref="Vector{T}"/> lanes at a time so that the constant is a store per lane group rather than a
    /// store per element.
    /// </remarks>
    internal static class IdentityFill
    {

        /// <summary>
        /// Writes <paramref name="first"/>, <paramref name="first"/> + 1, ... into <paramref name="destination"/>.
        /// Linear in its length.
        /// </summary>
        /// <param name="destination">The slots to fill.</param>
        /// <param name="first">The value written to the first slot: its identifier.</param>
        public static void Fill(Span<uint> destination, uint first)
        {
            var i = 0;
            var width = Vector<uint>.Count;
            if (Vector.IsHardwareAccelerated && destination.Length >= width * 2)
            {
                Span<uint> lanes = stackalloc uint[width];
                for (var j = 0; j < width; j++)
                    lanes[j] = first + (uint)j;

                var next = new Vector<uint>(lanes);
                var step = new Vector<uint>((uint)width);
                var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(destination);
                for (var j = 0; j < vectors.Length; j++)
                {
                    vectors[j] = next;
                    next += step;
                }

                i = vectors.Length * width;
            }

            for (; i < destination.Length; i++)
                destination[i] = first + (uint)i;
        }

    }

}
