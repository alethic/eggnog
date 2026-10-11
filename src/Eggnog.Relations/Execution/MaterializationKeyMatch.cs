using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="MaterializationEntry"/> of a probed key: compares the stored hash, then the stored key
    /// cells against the probe's.
    /// </summary>
    internal readonly struct MaterializationKeyMatch : IEntryMatch<MaterializationEntry>
    {

        readonly uint[] keys;
        readonly uint[] probe;
        readonly int offset;
        readonly int arity;
        readonly ulong hash;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keys">The materialization's key store: group <c>g</c>'s key at <c>[g * arity, (g + 1) * arity)</c>.</param>
        /// <param name="arity">The number of key cells.</param>
        /// <param name="probe">The array holding the probed key.</param>
        /// <param name="offset">The index of the probed key's first cell.</param>
        /// <param name="hash">The probed key's hash.</param>
        public MaterializationKeyMatch(uint[] keys, int arity, uint[] probe, int offset, ulong hash)
        {
            this.keys = keys;
            this.arity = arity;
            this.probe = probe;
            this.offset = offset;
            this.hash = hash;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in MaterializationEntry entry)
        {
            return entry.Hash == hash && new ReadOnlySpan<uint>(keys, entry.Group * arity, arity).SequenceEqual(new ReadOnlySpan<uint>(probe, offset, arity));
        }

    }

}
