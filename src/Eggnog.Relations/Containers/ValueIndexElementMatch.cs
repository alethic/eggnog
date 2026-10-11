using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Finds the <see cref="ContainerValueIndex"/> slot of one element value.
    /// </summary>
    internal readonly struct ValueIndexElementMatch : IEntryMatch<ValueIndexEntry>
    {

        readonly uint element;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="element">The element value looked up.</param>
        public ValueIndexElementMatch(uint element)
        {
            this.element = element;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(in ValueIndexEntry entry) => entry.Element == element;

    }

}
