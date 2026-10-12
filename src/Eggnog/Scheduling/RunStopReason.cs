namespace Eggnog
{

    /// <summary>
    /// Why a run ended.
    /// </summary>
    public enum RunStopReason
    {

        /// <summary>
        /// The schedule ran to its end.
        /// </summary>
        Completed,

        /// <summary>
        /// The run reached <see cref="RunOptions.IterationLimit"/>.
        /// </summary>
        IterationLimit,

        /// <summary>
        /// The scope became contradictory and the run stops on contradiction.
        /// </summary>
        Contradiction,

    }

}
