namespace Eggnog.Relations
{

    /// <summary>
    /// The kind of a <see cref="JoinStage"/>: the variants of egglog's <c>JoinStage</c> enum
    /// (core-relations/src/free_join/plan.rs:133-157). Interpreters switch on it and reinterpret the stage with
    /// <c>Unsafe.As</c> instead of making a virtual call.
    /// </summary>
    internal enum JoinStageKind : byte
    {

        /// <summary>
        /// A generic-join step on one variable: <see cref="IntersectStage"/>.
        /// </summary>
        Intersect,

        /// <summary>
        /// A Free Join step: scan a cover sub-atom and probe other sub-atoms, <see cref="FusedIntersectStage"/>.
        /// </summary>
        FusedIntersect,

        /// <summary>
        /// A scan or lookup of a materialization of a decomposed plan: <see cref="FusedIntersectMatStage"/>.
        /// </summary>
        FusedIntersectMat,

    }

}
