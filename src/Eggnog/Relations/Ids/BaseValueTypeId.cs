using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a registered base value type.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    internal readonly record struct BaseValueTypeId(int Rep) : INumericId<BaseValueTypeId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BaseValueTypeId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "bt" + Rep;

    }

}
