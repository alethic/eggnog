using System;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Captures the epoch of a rule run, rebuild pass or write batch (colored-contexts spec §4.1, C1, X3): the
        /// store clock's current value. Every write of the run, pass or batch is stamped with it, through the states it
        /// creates (<see cref="ExecutionState.Timestamp"/>, which <see cref="ExecutionState.ReadCounter"/> returns for
        /// the clock) and the merges it runs. Pair with <see cref="EndEpoch"/>.
        /// </summary>
        /// <remarks>
        /// The epochs of one context never decrease (I3): it captures a monotone counter, and its operations run one at
        /// a time in its domain. A sibling advancing the clock concurrently does not change an epoch already captured.
        /// </remarks>
        /// <returns>The epoch.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen or faulted.</exception>
        /// <exception cref="OverflowException">The clock has run out.</exception>
        internal uint BeginEpoch()
        {
            ThrowIfDisposed();
            if (frozen)
                throw new InvalidOperationException("A frozen context cannot begin an epoch: it cannot be written.");

            ThrowIfFaulted();
            return store.ReadClock();
        }

        /// <summary>
        /// Ends the epoch <see cref="BeginEpoch"/> captured by advancing the store clock: egglog-bridge's <c>inc_ts</c>.
        /// A rule run that grew the union-find leaves its epoch to the first rebuild pass instead (X4).
        /// </summary>
        internal void EndEpoch() => store.AdvanceClock();

        /// <summary>
        /// Records that a rule run of this context captured <paramref name="epoch"/>, raising
        /// <see cref="SemiNaiveFloor"/> (§7.2). Called by the bridge at the start of each run.
        /// </summary>
        /// <param name="epoch">The run's epoch.</param>
        internal void NoteRuleRun(uint epoch)
        {
            if (epoch > semiNaiveFloor)
                semiNaiveFloor = epoch;
        }

        /// <summary>
        /// Gets the floor below which an ancestor's newly caught-up row would escape this context's semi-naive
        /// evaluation: <c>max(CreatedAt, the largest epoch passed to NoteRuleRun)</c> (§7.2). Every watermark the
        /// context can use is at most this.
        /// </summary>
        internal uint SemiNaiveFloor => semiNaiveFloor;

    }

}
