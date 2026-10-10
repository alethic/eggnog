using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies the action (right-hand side) of a rule.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    public readonly record struct ActionId(int Rep) : INumericId<ActionId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ActionId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "act" + Rep;

    }

}
