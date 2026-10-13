namespace Eggnog.Relations
{

    /// <summary>
    /// The kind of a <see cref="PlanTemplate"/>: the variants of egglog's <c>Plan</c> enum
    /// (core-relations/src/free_join/plan.rs:211-215).
    /// </summary>
    internal enum PlanKind : byte
    {

        /// <summary>
        /// One stage list over the whole query: <see cref="SinglePlan"/>.
        /// </summary>
        Single,

        /// <summary>
        /// A chain of materializing blocks plus a result block: <see cref="DecomposedPlan"/>.
        /// </summary>
        Decomposed,

    }

}
