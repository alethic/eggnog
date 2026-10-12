using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a registered container type.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    internal readonly record struct ContainerTypeId(int Rep) : INumericId<ContainerTypeId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ContainerTypeId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "ct" + Rep;

    }

}
