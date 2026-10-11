using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a rule registered in an <see cref="EGraph"/>: egglog-bridge's <c>RuleId</c>
    /// (egglog-bridge/src/lib.rs:104), named apart from the core rule set's <see cref="RuleId"/>. One e-graph rule becomes
    /// several core rules each time it runs (its semi-naive variants). Ids are reserved by <see cref="EGraph.NewRule"/>
    /// and reused after <see cref="EGraph.FreeRule"/>.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    public readonly record struct EGraphRuleId(int Rep) : INumericId<EGraphRuleId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EGraphRuleId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "rule" + Rep;

    }

}
