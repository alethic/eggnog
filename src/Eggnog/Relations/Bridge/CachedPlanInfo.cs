using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The plan of an e-graph rule, built once at the rule's first run: the core <see cref="CachedPlan"/> (the planned
    /// query and the lowered action program) and, for each rule atom, the core atom it became, to which the semi-naive
    /// timestamp constraints of every run are attached. egglog-bridge's <c>CachedPlanInfo</c>
    /// (egglog-bridge/src/lib.rs:974-980).
    /// </summary>
    /// <remarks>
    /// Never invalidated: the join order is fixed by the table statistics at the rule's first run, as in egglog.
    /// Immutable, so it may be shared by any number of contexts.
    /// </remarks>
    internal sealed class CachedPlanInfo
    {

        readonly AtomId[] atomMapping;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="plan">The core plan.</param>
        /// <param name="atomMapping">The core atom of each rule atom, by <see cref="RuleAtomId"/>; owned by the new
        /// instance.</param>
        public CachedPlanInfo(CachedPlan plan, AtomId[] atomMapping)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(atomMapping);
            Plan = plan;
            this.atomMapping = atomMapping;
        }

        /// <summary>
        /// Gets the core plan.
        /// </summary>
        public CachedPlan Plan { get; }

        /// <summary>
        /// Gets the core atom of each rule atom, indexed by <see cref="RuleAtomId"/>.
        /// </summary>
        public ReadOnlySpan<AtomId> AtomMapping => atomMapping;

    }

}
