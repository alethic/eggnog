using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="ColumnIndexEntry"/> of one value.
    /// </summary>
    internal readonly struct ColumnIndexKeyMatch : IEntryMatch<ColumnIndexEntry>
    {

        readonly uint key;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="key">The value sought.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ColumnIndexKeyMatch(uint key)
        {
            this.key = key;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in ColumnIndexEntry entry) => entry.Key == key;

    }

}
