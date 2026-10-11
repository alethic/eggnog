namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The merge function of a table in a differential program (<see cref="DiffTable"/>). Each maps to one of the
    /// engine's merge functions, and <see cref="DiffModelTable"/> reproduces its semantics on a plain dictionary.
    /// </summary>
    enum DiffMergeKind
    {

        /// <summary>
        /// Always replaces the row with the incoming one, even an identical one (<see cref="ClosureMerge.TakeNew"/>),
        /// which leaves a tombstone per re-insert: the churn that drives compaction.
        /// </summary>
        TakeNew,

        /// <summary>
        /// Keeps the row the table holds (<see cref="KeepCurrentMerge"/>, egglog's <c>:merge old</c>).
        /// </summary>
        KeepCurrent,

        /// <summary>
        /// Replaces the row when the value column differs (<see cref="KeepIncomingMerge"/>, egglog's <c>:merge new</c>).
        /// </summary>
        KeepIncoming,

        /// <summary>
        /// Keeps the smaller value of the value column (<see cref="MinMerge"/>).
        /// </summary>
        Min,

        /// <summary>
        /// Keeps the larger value of the value column (<see cref="MaxMerge"/>).
        /// </summary>
        Max,

        /// <summary>
        /// Unions the two ids of the value column into the union-find and keeps the smaller (<see cref="UnionIdMerge"/>).
        /// </summary>
        UnionId,

    }

}
