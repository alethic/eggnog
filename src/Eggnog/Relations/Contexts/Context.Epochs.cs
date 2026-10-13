using System;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Captures the epoch of a rule run, rebuild pass or write batch: the store clock's current value. Every write
        /// of the run, pass or batch is stamped with it, through the states it creates
        /// (<see cref="ExecutionState.Timestamp"/>, which <see cref="ExecutionState.ReadCounter"/> returns for the
        /// clock) and the merges it runs. Pair with <see cref="EndEpoch"/>.
        /// </summary>
        /// <remarks>
        /// The epochs of one context never decrease: it captures a monotone counter, and its operations run one at a
        /// time in its domain. A sibling advancing the clock concurrently does not change an epoch already captured.
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
            var epoch = store.ReadClock();
            if (epoch > lastEpoch)
                lastEpoch = epoch;

            return epoch;
        }

        /// <summary>
        /// Checks that a rule run or merge of this child at <paramref name="epoch"/>, a timestamp its caller chose,
        /// keeps the context's epochs nondecreasing and its rows at or above its floor, then records it as the
        /// context's latest epoch. The floor is the larger of <see cref="SemiNaiveFloor"/> (its creation, below which
        /// none of its rows may be stamped, and its last noted run, below which no new row may be stamped, which the
        /// rule watermarks rely on) and the latest epoch it captured or ran at (so every layer's sort column stays
        /// nondecreasing, which a layer's merge would otherwise enforce by throwing and faulting the child).
        /// </summary>
        /// <param name="epoch">The timestamp the run's states and its merge carry.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="epoch"/> is below the floor; nothing is
        /// recorded.</exception>
        internal void UseEpoch(uint epoch)
        {
            var floor = Math.Max(semiNaiveFloor, lastEpoch);
            if (epoch < floor)
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch, $"A rule run of a child context must carry a timestamp of at least {floor}: its creation ({createdAt}), its last noted rule run and its latest epoch.");

            lastEpoch = epoch;
        }

        /// <summary>
        /// Ends the epoch <see cref="BeginEpoch"/> captured by advancing the store clock: egglog-bridge's
        /// <c>inc_ts</c>. A rule run that grew the union-find leaves its epoch to the first rebuild pass instead, as
        /// egglog-bridge does, so the context's epochs are nondecreasing rather than strictly increasing.
        /// </summary>
        internal void EndEpoch() => store.AdvanceClock();

        /// <summary>
        /// Records that a rule run of this context captured <paramref name="epoch"/>, raising
        /// <see cref="SemiNaiveFloor"/>. Called by the bridge at the start of each run.
        /// </summary>
        /// <param name="epoch">The run's epoch.</param>
        internal void NoteRuleRun(uint epoch)
        {
            if (epoch > semiNaiveFloor)
                semiNaiveFloor = epoch;
        }

        /// <summary>
        /// Gets the floor below which an ancestor's newly caught-up row would escape this context's semi-naive
        /// evaluation: <c>max(CreatedAt, the largest epoch passed to NoteRuleRun)</c>. Every watermark the context can
        /// use is at most this.
        /// </summary>
        internal uint SemiNaiveFloor => semiNaiveFloor;

    }

}
