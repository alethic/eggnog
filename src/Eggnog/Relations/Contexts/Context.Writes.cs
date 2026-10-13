using System;

namespace Eggnog.Relations
{

    internal sealed partial class Context
    {

        /// <summary>
        /// Runs <paramref name="action"/> as one write batch of this context: inside the context's domain, after
        /// catching up with the parent, with a fresh execution state stamped with a captured epoch; then flushes the
        /// state, records its failure, merges, and ends the epoch.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The write leaves rebuild work pending: a union makes the context not clean until <see cref="Rebuild"/>. At
        /// the root this is egglog's raw write: rows are stored as given. In a child an exception escaping the batch
        /// faults the context, because a hide whose copy was never merged cannot be repaired; the root keeps
        /// <see cref="Relations.Database"/>'s own exception guarantee. Panics reported through
        /// <see cref="ExecutionState.ReportFailure"/> are not exceptions and are recorded in
        /// <see cref="Relations.Database.Failure"/>.
        /// </para>
        /// <para>
        /// The action runs as a callback of the context's thread domain: inside it, reads of any context are allowed,
        /// but a write to a context of the same domain (a nested <c>Write</c> or <see cref="Union"/>) and a rebuild
        /// that would run a pass are refused with <see cref="InvalidOperationException"/> before they change anything,
        /// as are <see cref="CreateChild"/>, <see cref="Freeze"/> and <see cref="Dispose"/>. Such a nested operation
        /// would run an epoch of its own inside the batch's, or change the parent the batch caught up with, and the
        /// batch's own merge would then fail its order check or run against a stale parent.
        /// </para>
        /// <para>
        /// In a child the rows merge into the child's layers: each incoming row's id cells are canonicalized in the
        /// child and its sort cell set to the batch's epoch before it is stored, so a write naming a stale id lands on
        /// the canonical key and merges with the row visible there. At the root rows are stored as given, and DEBUG
        /// builds assert that every staged row of a registered table carries the batch's epoch
        /// (<see cref="SortedWritesTable.AssertPendingSortCells"/>). Every hidden set the merge touched is sealed
        /// before the batch returns.
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
                var state = database.CreateEngineState(epoch);
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
                if (parent is null)
                    AssertRootSortCells(epoch);

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
        /// Brings the context up to date before anything is staged in it: rebuilds a parent that is not frozen, then,
        /// in a child that is behind its parent's union-find, some ancestor of which changed since it last caught up
        /// (<see cref="ContextTables.NeedsCatchUp"/>), or whose eq-container guard has not seen every eq-sort container
        /// (<see cref="IsEqGuardCurrent"/>), rebuilds the child, which catches up first. The child's own pending
        /// rebuild work (unions merged since its last rebuild) is left for <see cref="Rebuild"/>. Nothing at the root.
        /// </summary>
        /// <exception cref="InvalidOperationException">The parent was stopped by a contradiction: a child that stops on
        /// contradictions is then stopped, any other is refused; neither is faulted.</exception>
        internal void PrepareForWrite()
        {
            if (parent is null)
                return;

            if (parent.IsFrozen == false)
            {
                parent.Rebuild();
                StopIfParentStopped(parent);
                ThrowIfContradicted();
            }

            if (IsUnionFindCurrent == false || tables!.NeedsCatchUp || IsEqGuardCurrent == false)
                Rebuild();
        }

        /// <summary>
        /// Removes every row of <paramref name="table"/> from this context's view. At the root, egglog's
        /// <c>clear_table</c> (<see cref="Relations.Database.ClearTable"/>); in a child, the child's layer is cleared
        /// and every ancestor layer of a shared table disappears from its views
        /// (<see cref="ContextTables.ClearTable"/>).
        /// </summary>
        /// <remarks>
        /// The retraction guard refuses the clear of a shared table while the context has live descendants, and in a
        /// child whose parent is not frozen; a refusal changes nothing and does not fault the child. Runs inside the
        /// context's domain, after catching up with the parent.
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is frozen, faulted or contradicted; the clear is
        /// refused (by the retraction guard, or because the table's merge is not a lattice merge, which only the root
        /// may write); the domain is held by another thread; or the call comes from inside a write batch's action or a
        /// <c>ScanMerges</c> visitor of the same domain.</exception>
        public void ClearTable(TableId table)
        {
            ThrowIfNotWritable();
            var current = Domain;
            using var scope = current.Enter();
            ThrowIfNotWritable();
            ThrowIfInCallback(current, nameof(ClearTable));
            if (tables is not { } t)
            {
                database.ClearTable(table);
                return;
            }

            if (table == store.UnionFindTable)
                throw new InvalidOperationException($"The union-find ({Store.UnionFindName}) of a child context cannot be cleared: its displacements are never retracted.");

            PrepareForWrite();

            // The checks of ContextTables.ClearTable run before it changes anything; a failure after them faults.
            t.Guard.CheckClear(table);
            t.GetOrCreateLayer(table);

            try
            {
                t.ClearTable(table);
            }
            catch
            {
                Fault();
                throw;
            }
        }

        /// <summary>
        /// DEBUG builds: asserts the root's raw-write contract (every staged row carries the batch's epoch) on every
        /// registered table before a root batch merges.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        void AssertRootSortCells(uint epoch)
        {
            foreach (var id in store.Schema.FunctionTables)
                store.Schema[id]!.Root.AssertPendingSortCells(epoch);
        }

    }

}
