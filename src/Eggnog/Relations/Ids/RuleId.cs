using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a rule within a rule set.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    internal readonly record struct RuleId(int Rep) : INumericId<RuleId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RuleId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "r" + Rep;

    }

}
