namespace Eggnog.Relations
{

    /// <summary>
    /// The algorithm the planner uses to order the stages of one bag: egglog's <c>PlanStrategy</c>
    /// (core-relations/src/free_join/plan.rs:390-410).
    /// </summary>
    /// <remarks>
    /// <see cref="Gj"/> is the default (value zero, as egglog's <c>#[default]</c>); egglog's bridge uses it for every
    /// rule with more than one atom, and <see cref="MinCover"/> for single-atom and rebuild rules.
    /// </remarks>
    internal enum PlanStrategy : byte
    {

        /// <summary>
        /// Generic Join: one variable per stage, intersecting every column that holds it (<see cref="IntersectStage"/>).
        /// </summary>
        Gj = 0,

        /// <summary>
        /// Free Join: repeatedly take the smallest atom not yet fully bound as the cover of the next stage.
        /// </summary>
        PureSize = 1,

        /// <summary>
        /// Free Join: first restrict the covers to a greedy approximate minimum set cover of the variables, then order
        /// them by size.
        /// </summary>
        MinCover = 2,

    }

}
