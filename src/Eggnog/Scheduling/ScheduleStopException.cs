using System;

namespace Eggnog
{

    /// <summary>
    /// Unwinds a run that reached a limit (D42): caught by <see cref="Scope.Run(Schedule, RunOptions, System.Threading.CancellationToken)"/>,
    /// which reports the reason. Never escapes the rule layer.
    /// </summary>
    internal sealed class ScheduleStopException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="reason">Why the run stops.</param>
        public ScheduleStopException(RunStopReason reason)
            : base("The run stopped: " + reason)
        {
            Reason = reason;
        }

        /// <summary>
        /// Gets why the run stops.
        /// </summary>
        public RunStopReason Reason { get; }

    }

}
