using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Collects the matches the join produces for each action into batches and runs a batch's program once it is full:
    /// the state behind egglog's <c>InPlaceActionBuffer</c> and <c>ScopedActionBuffer</c>
    /// (core-relations/src/free_join/execute.rs:1884-2049, 2095-2109). <see cref="InPlaceActionSink"/> and
    /// <see cref="ScopedActionSink"/> wrap it for the join.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A full batch (<see cref="Bindings.DefaultBatchSize"/> matches) runs on a fresh clone of the root execution state
    /// (fresh mutation buffers and an empty predicted-value cache, decision 5), which is flushed afterwards: inline
    /// when the buffer has no <see cref="WorkScope"/> (or the scope is backlogged), otherwise as a task of the scope,
    /// with the batch handed over and replaced. <see cref="Flush"/> runs the partial batches left at the end.
    /// </para>
    /// <para>
    /// Not thread-safe: each join task owns its buffer (<see cref="CreateTaskLocal"/>). The match counter is shared.
    /// </para>
    /// </remarks>
    internal sealed class ActionBuffer
    {

        readonly ActionInfo[] actions;
        readonly MatchCounter counter;
        readonly ExecutionState root;
        readonly WorkScope? scope;
        ActionBatch?[] batches = Array.Empty<ActionBatch?>();
        bool needsFlush;

        /// <summary>
        /// Initializes an empty buffer.
        /// </summary>
        /// <param name="actions">The program of each action, by action index; shared, not copied.</param>
        /// <param name="counter">The match counter.</param>
        /// <param name="root">The state whose clones run the batches.</param>
        /// <param name="scope">The scope full batches are spawned into, or <c>null</c> to run them inline.</param>
        public ActionBuffer(ActionInfo[] actions, MatchCounter counter, ExecutionState root, WorkScope? scope)
        {
            ArgumentNullException.ThrowIfNull(actions);
            ArgumentNullException.ThrowIfNull(counter);
            ArgumentNullException.ThrowIfNull(root);
            this.actions = actions;
            this.counter = counter;
            this.root = root;
            this.scope = scope;
        }

        /// <summary>
        /// Gets the scope full batches are spawned into, or <c>null</c>.
        /// </summary>
        public WorkScope? Scope => scope;

        /// <summary>
        /// Gets whether matches were pushed since the last <see cref="Flush"/>. egglog's <c>needs_flush</c>.
        /// </summary>
        public bool NeedsFlush => needsFlush;

        /// <summary>
        /// Creates an empty buffer for another join task, sharing the programs, the counter, the root state and the
        /// scope. egglog's <c>ScopedActionBuffer</c> built inside <c>recur</c>.
        /// </summary>
        /// <returns>The buffer.</returns>
        public ActionBuffer CreateTaskLocal() => new(actions, counter, root, scope);

        /// <summary>
        /// Appends a match of <paramref name="action"/> and runs the batch if it is full. egglog's
        /// <c>push_bindings</c>. The first match of a batch maps every variable in <paramref name="bound"/>; later
        /// matches write the action's used variables only (<see cref="Bindings.Push(ReadOnlySpan{uint}, ReadOnlySpan{Variable}, ReadOnlySpan{Variable})"/>).
        /// Amortized O(used variables), plus the batch's program every <see cref="Bindings.DefaultBatchSize"/> matches.
        /// </summary>
        /// <param name="action">The action's index.</param>
        /// <param name="values">The join's bindings, indexed by variable.</param>
        /// <param name="bound">The variables the join has bound; the same array for every match of the action.</param>
        public void Push(int action, uint[] values, Variable[] bound)
        {
            needsFlush = true;
            if ((uint)action >= (uint)batches.Length)
                GrowBatches(action);

            var batch = batches[action] ??= new ActionBatch();
            var info = actions[action];
            batch.Bindings.Push(values, bound, info.UsedVariables);
            if (++batch.Length < Bindings.DefaultBatchSize)
                return;

            // Without a scope, or when the scope is backlogged, run the batch inline (see WorkScope.IsBacklogged).
            if (scope is null || scope.IsBacklogged)
            {
                var state = root.Clone();
                var succeeded = state.RunInstrs(info.Instrs, batch.Bindings);
                state.Flush();
                batch.Bindings.Clear();
                batch.Length = 0;
                counter.Add(action, succeeded);
                return;
            }

            var full = batch.Bindings;
            batch.Bindings = new Bindings(Bindings.DefaultBatchSize);
            batch.Length = 0;
            scope.Spawn(new ActionBatchWork(root.Clone(), info, full, counter, action));
        }

        /// <summary>
        /// Runs every partial batch on <paramref name="state"/> and clears it. egglog's <c>flush_action_states</c>.
        /// The caller flushes <paramref name="state"/> afterwards.
        /// </summary>
        /// <param name="state">The state to run the batches on.</param>
        public void Flush(ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            for (var action = 0; action < batches.Length; action++)
            {
                var batch = batches[action];
                if (batch is null || batch.Length == 0)
                    continue;

                var succeeded = state.RunInstrs(actions[action].Instrs, batch.Bindings);
                batch.Bindings.Clear();
                batch.Length = 0;
                counter.Add(action, succeeded);
            }

            needsFlush = false;
        }

        /// <summary>
        /// Makes room for the batch of <paramref name="action"/>: batches are created per action on first use, as
        /// egglog's <c>DenseIdMap</c> of them is, so a task's buffer costs nothing for the actions it never feeds.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void GrowBatches(int action)
        {
            if ((uint)action >= (uint)actions.Length)
                throw new ArgumentOutOfRangeException(nameof(action), action, "The action is not part of the rule set.");

            Array.Resize(ref batches, Math.Min(actions.Length, Math.Max(action + 1, batches.Length * 2)));
        }

        /// <summary>
        /// Runs every partial batch on one fresh clone of the root state and flushes that state: egglog's
        /// <c>flush(&amp;mut exec_state.clone())</c>.
        /// </summary>
        public void FlushOnClone()
        {
            var state = root.Clone();
            Flush(state);
            state.Flush();
        }

    }

}
