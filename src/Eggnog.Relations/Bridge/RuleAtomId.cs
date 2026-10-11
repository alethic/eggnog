using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies an atom of a rule built with an <see cref="EGraphRuleBuilder"/>: egglog-bridge's <c>AtomId</c>
    /// (egglog-bridge/src/rule.rs:25), named apart from the core query's <see cref="AtomId"/>. Atoms are numbered from
    /// zero in the order they are added, which is also the order of the rule's semi-naive variants.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    public readonly record struct RuleAtomId(int Rep) : INumericId<RuleAtomId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RuleAtomId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "atom" + Rep;

    }

}
