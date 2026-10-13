namespace Eggnog
{

    /// <summary>
    /// How a ruleset's rules are checked: passed to <see cref="EGraph.DefineRuleset(string, RulesetOptions)"/>.
    /// </summary>
    public sealed class RulesetOptions
    {

        /// <summary>
        /// Gets the licence tag under which every rule of the ruleset is erasing (D12): every atom over a fact table must
        /// read the licence scope (<c>In(licence)</c>), unless the rule calls <see cref="RuleBuilder.ReadsOwnFacts"/>.
        /// <c>null</c>, the default, for none. A tag of another graph is refused with
        /// <see cref="System.ArgumentException"/> when the ruleset is defined.
        /// </summary>
        public ScopeTag? Licence { get; init; }

    }

}
