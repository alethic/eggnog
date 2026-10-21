using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// Per-scope buffers that external functions write during engine calls: the tuples of a query or split matcher, those
    /// of a scheduler's queries, the tables rule callbacks deleted from, and the first evaluation failure. Thread-safe,
    /// because rule tasks may run in parallel.
    /// </summary>
    /// <remarks>
    /// Every engine context has its own, the value of the graph's context-local slot
    /// (<see cref="EGraph.RuntimeSlot"/>), reached from the execution state the engine passes to an external function:
    /// the state's view's database belongs to the context the call runs in. A query's transient child gets its own
    /// runtime, whose <see cref="Owner"/> is the queried scope.
    /// </remarks>
    internal sealed class ScopeRuntime
    {

        readonly object sync = new();
        EvaluationFailure? failure;
        ConcurrentDictionary<TableHandle, byte>? callbackDeletions;

        /// <summary>
        /// Gets the runtime of the context an engine call runs in:
        /// <c>(state.View?.Owner.Owner?.Host as EGraphContext)?.GetLocal(graph.RuntimeSlot)</c>, or the root's for
        /// a state with no context of this graph. Lock-free once the runtime exists: a few field reads and one volatile
        /// read; safe from any rule task.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="state">The execution state the engine passed to the external function.</param>
        /// <returns>The runtime.</returns>
        public static ScopeRuntime Of(EGraph graph, Relations.ExecutionState state)
        {
            if (state.View?.Owner.Owner?.Host is Relations.EGraphContext context && ReferenceEquals(context.Owner, graph.Engine))
                return context.GetLocal(graph.RuntimeSlot);

            return graph.Root.Runtime;
        }

        /// <summary>
        /// Gets or sets the scope this runtime belongs to.
        /// </summary>
        public Scope? Owner { get; set; }

        /// <summary>
        /// Records a failure unless one is recorded already: the first one wins. Thread-safe.
        /// </summary>
        /// <param name="failure">The failure.</param>
        /// <returns>Whether it was recorded.</returns>
        public bool TryRecord(EvaluationFailure failure)
        {
            lock (sync)
            {
                if (this.failure is not null)
                    return false;

                this.failure = failure;
                return true;
            }
        }

        /// <summary>
        /// Takes the recorded failure, if any, and clears it. Thread-safe.
        /// </summary>
        /// <returns>The failure, or <c>null</c>.</returns>
        public EvaluationFailure? TakeFailure()
        {
            lock (sync)
            {
                var taken = failure;
                failure = null;
                return taken;
            }
        }

        /// <summary>
        /// Gets or sets the buffer a query or split matcher's collector fills during one engine call.
        /// </summary>
        public TupleBuffer? QueryMatches { get; set; }

        /// <summary>
        /// Gets or sets whether the collector stops the engine after the first match (<c>Any</c>).
        /// </summary>
        public bool StopAtFirstQueryMatch { get; set; }

        /// <summary>
        /// Gets or sets the buffers schedulers' queries fill during one engine call, by rule slot
        /// (<see cref="ScheduledRule.Slot"/>); read-only while the call runs, so rule tasks may look them up concurrently.
        /// </summary>
        public Dictionary<int, SchedulerMatchBuffer>? SchedulerMatches { get; set; }

        /// <summary>
        /// Records that a rule callback deleted rows of <paramref name="table"/> in this engine call, so that the
        /// iteration reconciles the scope's mirrors of it as for a rule that deletes. Thread-safe.
        /// </summary>
        /// <param name="table">The table.</param>
        public void NoteCallbackDeletion(TableHandle table) => (callbackDeletions ?? InitDeletions()).TryAdd(table, 0);

        /// <summary>
        /// Takes the tables rule callbacks deleted rows of since the last call; <c>null</c> when there is none.
        /// </summary>
        /// <returns>The tables.</returns>
        public ICollection<TableHandle>? TakeCallbackDeletions()
        {
            if (callbackDeletions is not { IsEmpty: false } deletions)
                return null;

            var taken = new List<TableHandle>(deletions.Keys);
            deletions.Clear();
            return taken;
        }

        /// <summary>
        /// Makes the set of tables callbacks deleted from, once, on any thread.
        /// </summary>
        /// <returns>The set.</returns>
        ConcurrentDictionary<TableHandle, byte> InitDeletions()
        {
            Interlocked.CompareExchange(ref callbackDeletions,
                new ConcurrentDictionary<TableHandle, byte>(ReferenceEqualityComparer.Instance), null);
            return callbackDeletions!;
        }

    }

}
