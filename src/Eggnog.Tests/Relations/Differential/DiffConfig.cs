using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// How a differential rule is planned: the strategy, whether tree decomposition is off, and whether the rule is
    /// added from a cached plan (<see cref="RuleSetBuilder.AddRuleFromCachedPlan"/>) rather than planned in place.
    /// </summary>
    /// <param name="Strategy">The stage-ordering strategy.</param>
    /// <param name="NoDecomposition">Whether the planner skips tree decomposition.</param>
    /// <param name="Cached">Whether the rule is planned in a throwaway rule set and added from its cached plan.</param>
    readonly record struct DiffConfig(PlanStrategy Strategy, bool NoDecomposition, bool Cached)
    {

        /// <summary>
        /// Gets the six combinations of strategy and decomposition, planned in place.
        /// </summary>
        public static IReadOnlyList<DiffConfig> All { get; } =
            (from s in new[] { PlanStrategy.Gj, PlanStrategy.PureSize, PlanStrategy.MinCover }
             from d in new[] { false, true }
             select new DiffConfig(s, d, false)).ToArray();

        /// <inheritdoc />
        public override string ToString() => Strategy + (NoDecomposition ? "/nodecomp" : "") + (Cached ? "/cached" : "");

    }

}
