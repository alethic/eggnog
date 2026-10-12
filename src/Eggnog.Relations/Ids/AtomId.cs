using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies an atom of a query.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    internal readonly record struct AtomId(int Rep) : INumericId<AtomId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AtomId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "a" + Rep;

    }

}
