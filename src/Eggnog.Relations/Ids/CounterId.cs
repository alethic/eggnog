using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a counter of a <see cref="Database"/>.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    public readonly record struct CounterId(int Rep) : INumericId<CounterId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CounterId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "ctr" + Rep;

    }

}
