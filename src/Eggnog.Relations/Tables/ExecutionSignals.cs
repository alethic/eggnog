using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The state an <see cref="ExecutionState"/> shares with all of its clones: egglog's <c>stop_match</c> flag
    /// (core-relations/src/action/mod.rs:366-368, 582-595) plus the first failure reported while executing, where egglog
    /// routes a merge-time panic (an <c>AssertEq</c> conflict, a failed primitive) through a panic external function
    /// that records the message and triggers the early stop.
    /// </summary>
    /// <remarks>
    /// Thread-safe: parallel merges and rule tasks report through their clones at the same time. The stop flag is
    /// written with release and read with acquire semantics, as egglog does; the first failure wins.
    /// </remarks>
    internal sealed class ExecutionSignals
    {

        int stop;
        string? failure;

        /// <summary>
        /// Gets whether early stopping was requested.
        /// </summary>
        public bool ShouldStop => Volatile.Read(ref stop) != 0;

        /// <summary>
        /// Gets the first failure reported, or <c>null</c>.
        /// </summary>
        public string? Failure => Volatile.Read(ref failure);

        /// <summary>
        /// Requests early stopping.
        /// </summary>
        public void TriggerEarlyStop() => Volatile.Write(ref stop, 1);

        /// <summary>
        /// Records <paramref name="message"/> unless a failure was already recorded, and requests early stopping.
        /// </summary>
        /// <param name="message">What went wrong.</param>
        public void ReportFailure(string message)
        {
            Interlocked.CompareExchange(ref failure, message, null);
            TriggerEarlyStop();
        }

    }

}
