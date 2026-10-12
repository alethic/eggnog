namespace Eggnog
{

    /// <summary>
    /// One engine variant of a program (D36): the bridge rule, the constants it was lowered with, and the external
    /// functions it owns.
    /// </summary>
    internal sealed class LoweredRule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id">The bridge rule.</param>
        /// <param name="description">The variant's description, unique in the graph.</param>
        /// <param name="semiNaive">Whether the variant is semi-naive.</param>
        /// <param name="constants">The values the class constants were lowered with.</param>
        /// <param name="owned">The call sites registered for the variant, freed with it.</param>
        /// <param name="rule">The rule; <c>null</c> for a query, a split matcher or a copy rule.</param>
        public LoweredRule(Relations.EGraphRuleId id, string description, bool semiNaive, uint[] constants, Relations.ExternalFunctionId[] owned, Rule? rule)
        {
            Id = id;
            Description = description;
            SemiNaive = semiNaive;
            Constants = constants;
            Owned = owned;
            Rule = rule;
        }

        /// <summary>
        /// Gets the bridge rule.
        /// </summary>
        public Relations.EGraphRuleId Id { get; }

        /// <summary>
        /// Gets the description: unique in the graph, it keys the engine's statistics (§6.4.3).
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets whether the variant is semi-naive.
        /// </summary>
        public bool SemiNaive { get; }

        /// <summary>
        /// Gets the values the class constants were lowered with.
        /// </summary>
        public uint[] Constants { get; }

        /// <summary>
        /// Gets the call sites registered for the variant, freed with it.
        /// </summary>
        public Relations.ExternalFunctionId[] Owned { get; }

        /// <summary>
        /// Gets the rule; <c>null</c> for a query, a split matcher or a copy rule.
        /// </summary>
        public Rule? Rule { get; }

        /// <inheritdoc />
        public override string ToString() => Description;

    }

}
