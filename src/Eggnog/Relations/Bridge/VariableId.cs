using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a variable of a rule built with an <see cref="EGraphRuleBuilder"/>: egglog-bridge's <c>VariableId</c>
    /// (egglog-bridge/src/rule.rs:24). Dense from zero within one rule, in creation order; the ids of different rules
    /// are unrelated.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    internal readonly record struct VariableId(int Rep) : INumericId<VariableId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VariableId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "var" + Rep;

    }

}
