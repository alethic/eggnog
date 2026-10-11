using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerEnv<TContainer>
    {

        /// <summary>
        /// Rebuilds every container in parallel, in two phases over the hash-cons shards. egglog's
        /// <c>apply_rebuild_nonincremental_parallel</c> (mod.rs:573-699). Requires exclusive access.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Phase one walks each shard on its own task with the same per-entry step as the serial rebuild
        /// (<see cref="ContainerRebuildPredicate{TContainer}"/>); a changed container goes to the outbox list of its
        /// source shard and destination shard. Phase two runs one task per destination shard, each with its own clone of
        /// <paramref name="state"/> (flushed when the task ends), and inserts that shard's queued containers, merging
        /// with equal ones. Only one task touches a hash-cons shard in either phase; the reverse map and the element
        /// index are shared and take their shard locks.
        /// </para>
        /// <para>
        /// egglog's per-destination <c>SegQueue</c>s fill in whatever order the tasks run. Here each source writes its
        /// own row of lists and each destination reads them in source order, so the reinsertion order, and with it which
        /// ids end up dirty, is the same from run to run; ids still depend on the parallel schedule of the rest of the
        /// engine, as decision 4 allows. Dirty ids are reported by destination shard, then in queue order.
        /// </para>
        /// </remarks>
        /// <param name="rebuilder">The rebuilder.</param>
        /// <param name="state">The state whose clones merge functions run in.</param>
        /// <returns>What changed.</returns>
        internal ContainerRebuildSummary ApplyRebuildNonincrementalParallel(Rebuilder rebuilder, ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(rebuilder);
            ArgumentNullException.ThrowIfNull(state);

            var shards = toId.Length;
            var outbox = new List<ContainerReinsert<TContainer>>?[shards * shards];
            var changed = new bool[shards];
            var dirty = new List<uint>?[shards];

            try
            {
                Parallel.For(0, shards, parallelism.Options, source =>
                {
                    var shard = toId[source];
                    lock (shard)
                    {
                        var predicate = new ContainerRebuildPredicate<TContainer>(this, rebuilder, outbox, source * shards, shards);
                        shard.Retain(ref predicate);
                        changed[source] = predicate.Changed;
                    }
                });

                Parallel.For(0, shards, parallelism.Options, state.Clone, (destination, _, local) =>
                {
                    for (var source = 0; source < shards; source++)
                    {
                        if (outbox[source * shards + destination] is not { } queue)
                            continue;

                        foreach (var item in CollectionsMarshal.AsSpan(queue))
                        {
                            var actual = InsertOwned(item.Container, item.Hash, item.Id, local);

                            // As in the serial path, only same-id changes of meaning need a refresh (mod.rs:668-672,
                            // 684-686).
                            if (item.Stable && actual == item.Id)
                                (dirty[destination] ??= new List<uint>()).Add(item.Id);
                        }
                    }

                    return local;
                }, local => local.Flush());
            }
            catch (AggregateException e)
            {
                var inner = e.Flatten().InnerExceptions;
                if (inner.Count == 1)
                    ExceptionDispatchInfo.Capture(inner[0]).Throw();

                throw;
            }

            var summary = new ContainerRebuildSummary();
            foreach (var flag in changed)
            {
                if (flag)
                {
                    summary.NoteChange();
                    break;
                }
            }

            foreach (var ids in dirty)
            {
                if (ids is null)
                    continue;

                foreach (var id in CollectionsMarshal.AsSpan(ids))
                    summary.NoteDirtyId(id);
            }

            return summary;
        }

    }

}
