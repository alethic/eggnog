using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The early stop of a case (colored-contexts spec §10.6, X11, L4): called by every rule right after it inserts into
    /// the e-graph's contradiction function, it stops the run's search when the running context stops on contradictions
    /// (<see cref="ContextOptions.StopOnContradiction"/>), so a vacuous case does no more work. Elsewhere it does nothing:
    /// for host runs a contradiction is an ordinary fact (rule-layer D11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It only cuts the search short. The stop flag itself is set by the merge, when the contradiction row lands
    /// (§6.9): batches already formed still run and the run still merges. It always produces a value (zero), so it never
    /// halts the lane it runs on; it reads no table.
    /// </para>
    /// <para>
    /// Stateless; safe on any number of threads. One check per call: the state's database's context options.
    /// </para>
    /// </remarks>
    internal sealed class StopOnContradictionFunction : ExternalFunction
    {

        /// <inheritdoc />
        public override bool TryInvoke(ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.View?.Owner.Owner is { Options.StopOnContradiction: true })
                state.TriggerEarlyStop();

            result = 0;
            return true;
        }

    }

}
