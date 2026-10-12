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
        /// <param name="runViews">The new rule set's pinned views (colored-contexts spec §9.1).</param>
        public void Rebind(ExecutionSignals runSignals, ViewSnapshot runViews)
        {
            ArgumentNullException.ThrowIfNull(runSignals);
            ArgumentNullException.ThrowIfNull(runViews);
            if (parallel)
                throw new InvalidOperationException("Only a serial join is kept between runs.");

            Debug.Assert(template is null, "a join was rebound in the middle of a rule");
            signals = runSignals;
            views = runViews;
        }

        /// <summary>
        /// Trims a serial join that finished its run normally, before the database keeps it for the next one: drops the
        /// references to the last rule's table views and hands leaf-scan buffers over <see cref="RetainedLeafCells"/> cells
        /// back to the pool. Everything else is already idle: <see cref="EndRule"/> released the nodes and cleared the
        /// frames. A join interrupted by an early stop or an exception is not kept.
        /// </summary>
        public void Park()
        {
            Debug.Assert(parallel == false && template is null, "only an idle serial join is kept between runs");
            Array.Clear(atomViews);
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
