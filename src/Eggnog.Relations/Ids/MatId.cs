using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a materialization produced by one block of a decomposed plan.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    public readonly record struct MatId(int Rep) : INumericId<MatId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MatId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "m" + Rep;

    }

}
