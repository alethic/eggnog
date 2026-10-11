using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// One block of a decomposed plan: the stages that evaluate one bag and the materialization they write. egglog's
    /// <c>(JoinStages, MatSpec)</c> pair (core-relations/src/free_join/plan.rs:277-281).
    /// </summary>
    /// <remarks>
    /// Block <c>i</c> writes materialization <c>MatId(i)</c>. Blocks run leaf first; a later block reads earlier
    /// materializations through <see cref="FusedIntersectMatStage"/> prologues and epilogues.
    /// </remarks>
    internal sealed class PlanBlock
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="stages">The stages; owned by the new instance.</param>
        /// <param name="spec">What the block materializes.</param>
        public PlanBlock(JoinStage[] stages, MatSpec spec)
        {
            ArgumentNullException.ThrowIfNull(stages);
            ArgumentNullException.ThrowIfNull(spec);
            Stages = stages;
            Spec = spec;
        }

        /// <summary>
        /// Gets the stages. Callers must not modify the array.
        /// </summary>
        public JoinStage[] Stages { get; }

        /// <summary>
        /// Gets what the block materializes.
        /// </summary>
        public MatSpec Spec { get; }

    }

}
