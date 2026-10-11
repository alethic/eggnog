namespace Eggnog.Relations
{

    /// <summary>
    /// One instruction of a join plan: egglog's <c>JoinStage</c> (core-relations/src/free_join/plan.rs:133-157).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closed hierarchy: <see cref="IntersectStage"/>, <see cref="FusedIntersectStage"/> and
    /// <see cref="FusedIntersectMatStage"/>, all sealed. <see cref="Kind"/> says which one a stage is, so an interpreter
    /// dispatches with a <c>switch</c> plus <c>Unsafe.As</c>, never a virtual call.
    /// </para>
    /// <para>
    /// Stages are immutable and shared by every instantiation of a plan template. Within one block each variable is
    /// bound by exactly one stage, and every stage intersects all occurrences (in the block) of the variables it binds,
    /// so stages commute except for materialization scans in modes <see cref="MatScanMode.Full"/>,
    /// <see cref="MatScanMode.Value"/> and <see cref="MatScanMode.Lookup"/>, which read variables bound earlier and are
    /// reorder barriers (see <see cref="StageOrdering"/>).
    /// </para>
    /// </remarks>
    internal abstract class JoinStage
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kind">The kind of the concrete stage.</param>
        private protected JoinStage(JoinStageKind kind)
        {
            Kind = kind;
        }

        /// <summary>
        /// Gets the kind of stage.
        /// </summary>
        public JoinStageKind Kind { get; }

    }

}
