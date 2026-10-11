using System;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Runs <paramref name="action"/> as one write batch of this context (§6.6): inside the context's domain, after
        /// catching up with the parent, with a fresh execution state stamped with a captured epoch; then flushes the
        /// state, records its failure, merges, and ends the epoch.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The write leaves rebuild work pending (the spike's semantics): a union makes the context not clean until
        /// <see cref="Rebuild"/>. At the root this is egglog's raw write: rows are stored as given. In a child an
        /// exception escaping the batch faults the context (X12); the root keeps <see cref="Relations.Database"/>'s own
        /// exception guarantee. Panics reported through <see cref="ExecutionState.ReportFailure"/> are not exceptions
        /// and are recorded in <see cref="Relations.Database.Failure"/>.
        /// </para>
        /// <para>
        /// The action runs as a callback of the context's thread domain (§8.3): inside it, reads of any context are
        /// allowed, but a write to a context of the same domain (a nested <c>Write</c> or <see cref="Union"/>) and a
        /// rebuild that would run a pass are refused with <see cref="InvalidOperationException"/> before they change
        /// anything, as are <see cref="CreateChild"/>, <see cref="Freeze"/> and <see cref="Dispose"/>. Such a nested
        /// operation would run an epoch of its own inside the batch's (I3), or change the parent the batch caught up
        /// with, and the batch's own merge would then fail its order check or run against a stale parent.
        /// </para>
        /// <para>
        /// Interim (M9.2, §17): in a child the state stages only into the union-find (its other tables have no layers
        /// until M9.3, and staging into them throws <see cref="NotSupportedException"/>, which faults the child). The
        /// DEBUG check of the root's sort cells and the canonicalizing, stamping merge of child rows arrive with the
        /// layers (M9.3, X38).
        /// </para>
        /// </remarks>
        /// <typeparam name="TResult">The action's result type.</typeparam>
        /// <param name="action">The batch: stages writes through the state it is given.</param>
        /// <returns>The action's result.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen, faulted or contradicted; its domain is held
        /// by another thread; or the call comes from inside a write batch's action or a <c>ScanMerges</c> visitor of the
        /// same domain.</exception>
        public TResult Write<TResult>(Func<ExecutionState, TResult> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Write(action, static (state, a) => a(state));
        }

        /// <summary>
        /// <see cref="Write{TResult}(Func{ExecutionState, TResult})"/> with an argument passed through, so a caller can use
        /// a static lambda and allocate no closure.
        /// </summary>
        /// <typeparam name="TArg">The argument type.</typeparam>
        /// <typeparam name="TResult">The action's result type.</typeparam>
        /// <param name="arg">The argument.</param>
        /// <param name="action">The batch.</param>
        /// <returns>The action's result.</returns>
        internal TResult Write<TArg, TResult>(TArg arg, Func<ExecutionState, TArg, TResult> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            ThrowIfNotWritable();
            var current = Domain;
            using var scope = current.Enter();
            ThrowIfNotWritable();
            ThrowIfInCallback(current, nameof(Write));
            PrepareForWrite();
            var epoch = BeginEpoch();
            try
            {
                var state = database.CreateExecutionState(epoch);
                TResult result;
                current.EnterCallback();
                try
                {
                    result = action(state, arg);
                }
                finally
                {
                    current.ExitCallback();
                }

                state.Flush();
                database.RecordFailure(state);
                database.MergeAll(epoch);
                return result;
            }
            catch when (IsRoot == false)
            {
                Fault();
                throw;
            }
            finally
            {
                EndEpoch();
            }
        }

        /// <summary>
        /// Brings the context up to date before anything is staged in it (§6.6): rebuilds a parent that is not frozen,
        /// then, in a child that is behind its parent's union-find, rebuilds the child, which catches up first. Nothing
        /// at the root.
        /// </summary>
        /// <remarks>Interim (M9.2): the row term (<c>Tables.NeedsCatchUp</c>) arrives with the layers (M9.3-M9.4).</remarks>
        internal void PrepareForWrite()
        {
            if (parent is null)
                return;

            if (parent.IsFrozen == false)
                parent.Rebuild();

            if (IsUnionFindCurrent == false)
                Rebuild();
        }

    }

}
