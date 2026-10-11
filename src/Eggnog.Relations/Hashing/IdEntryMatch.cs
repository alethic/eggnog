using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Recognizes the <see cref="IdEntry"/> of one id in a <see cref="RawHashTable{TEntry}"/>: one compare per
    /// candidate whose tag matched.
    /// </summary>
    internal readonly struct IdEntryMatch : IEntryMatch<IdEntry>
    {

        readonly uint id;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id">The id looked up.</param>
        public IdEntryMatch(uint id)
        {
            this.id = id;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in IdEntry entry) => entry.Id == id;

    }

}
