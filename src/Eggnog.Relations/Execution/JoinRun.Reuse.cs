using System;
using System.Diagnostics;

namespace Eggnog.Relations
{

    internal sealed partial class JoinRun
    {

        /// <summary>
        /// Leaf-scan buffers larger than this many cells are handed back to the shared array pool when an idle join is
        /// kept between runs (<see cref="Park"/>), so a database does not pin the largest leaf scan it ever ran.
        /// </summary>
        const int RetainedLeafCells = 1 << 16;

        /// <summary>
        /// Readies a serial join kept from an earlier run for a run whose states share <paramref name="runSignals"/>.
        /// The database keeps its serial join between rule sets, so the node pool (with each node's per-column indexes,
        /// refilled in place), the frame buffers, probers, scan buffers and scratch nodes are reused instead of being
        /// reallocated for every run; egglog takes the same storage from its thread-local pools.
        /// </summary>
        /// <param name="runSignals">The stop signal of the new run's states.</param>
        public void Rebind(ExecutionSignals runSignals)
        {
            ArgumentNullException.ThrowIfNull(runSignals);
            if (parallel)
                throw new InvalidOperationException("Only a serial join is kept between runs.");

            Debug.Assert(template is null, "a join was rebound in the middle of a rule");
            signals = runSignals;
        }

        /// <summary>
        /// Releases what an early stop left in a serial join's probers, so that the database can keep the join as it keeps
        /// one that ran to the end. An early stop (an action's <see cref="ExecutionState.TriggerEarlyStop"/>, or a failure
        /// it reported) returns from the stages at their checks and skips their <see cref="MoveBack"/>, so a prober still
        /// holds its node and, for a sparse or a per-stage tuple index, an index it owns. Those indexes go back to their
        /// pools and every prober is cleared. <see cref="EndRule"/> has already released the rule's nodes and cleared its
        /// frames and bindings; the frame-owned nodes the stop dropped are left to the garbage collector, as the pool
        /// leaves nodes beyond <see cref="TrieNodePool.MaxPooled"/>. Not in egglog, whose pooled buffers return to their
        /// pools when dropped however a run ends. O(positions × probers).
        /// </summary>
        public void ReleaseInterrupted()
        {
            Debug.Assert(parallel == false && template is null, "only an idle serial join is released after an early stop");
            for (var p = 0; p < probers.Length; p++)
            {
                if (probers[p] is not { } row)
                    continue;

                for (var j = 0; j < row.Length; j++)
                {
                    ref var prober = ref row[j];
                    if (prober.Kind == ProberKind.Sparse)
                        ReturnSparse(prober.Sparse!);
                    else if (prober.Kind == ProberKind.DynamicTuple)
                        prober.Tuple!.Dispose();

                    prober = default;
                }
            }
        }

        /// <summary>
        /// Trims a serial join that finished its run, before the database keeps it for the next one: drops the references
        /// to the last rule's tables and hands leaf-scan buffers over <see cref="RetainedLeafCells"/> cells back to the
        /// pool. Everything else is already idle: <see cref="EndRule"/> released the nodes and cleared the frames, and
        /// after an early stop <see cref="ReleaseInterrupted"/> cleared the probers. A join interrupted by an exception is
        /// not kept.
        /// </summary>
        public void Park()
        {
            Debug.Assert(parallel == false && template is null, "only an idle serial join is kept between runs");
            Array.Clear(tables);
            Array.Clear(infos);
            for (var p = 0; p < leafBuffers.Length; p++)
            {
                if (leafBuffers[p] is { } buffer && buffer.Data.Length > RetainedLeafCells)
                {
                    buffer.Dispose();
                    leafBuffers[p] = null;
                }
            }
        }

    }

}
