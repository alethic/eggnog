namespace Eggnog
{

    /// <summary>
    /// Why a run ended.
    /// </summary>
    /// <remarks>
    /// Each reason's number is fixed, so a reason stored or sent as an integer reads back as the same reason.
    /// </remarks>
    public enum RunStopReason
    {

        /// <summary>
        /// The schedule ran to its end.
        /// </summary>
        Completed = 0,

        /// <summary>
        /// The run reached <see cref="RunOptions.IterationLimit"/>.
        /// </summary>
        IterationLimit = 1,

        /// <summary>
        /// The run reached <see cref="RunOptions.TimeLimit"/>.
        /// </summary>
        TimeLimit = 3,

        /// <summary>
        /// The run reached <see cref="RunOptions.RowLimit"/>.
        /// </summary>
        RowLimit = 4,

    }

}
