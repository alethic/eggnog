using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies an external (host-provided) function registered with a <see cref="Database"/>.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    public readonly record struct ExternalFunctionId(int Rep) : INumericId<ExternalFunctionId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ExternalFunctionId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "f" + Rep;

    }

}
