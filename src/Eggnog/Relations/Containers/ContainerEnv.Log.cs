using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerEnv<TContainer>
    {

        /// <summary>
        /// The Added entries recorded for <see cref="ContainerEnvBase.Log"/> since its last drain, one list per hash-cons
        /// shard, each read and written only under that shard's lock; <c>null</c> until the first entry is recorded, so
        /// an environment whose log never activates allocates nothing. The array is read under a shard's lock as well:
        /// the recording that allocates it holds the lock of its entry's shard, so a drain, discard or count that takes
        /// that lock after the recording sees the array and the entry, and one that took it before comes before the
        /// recording's test of the log, which then sees the drain clear the pending flag and sets it again, or sees the
        /// clear deactivate the log and records nothing.
        /// </summary>
        List<ContainerLogEntry>?[]? logBuffers;

        /// <summary>
        /// Records that a container of classes appeared under <paramref name="id"/> in hash-cons shard
        /// <paramref name="shard"/>, while the log is active; at the root without live descendants, one volatile read.
        /// The caller holds the shard's lock.
        /// </summary>
        /// <param name="shard">The hash-cons shard of the entry.</param>
        /// <param name="id">The id the container appeared under.</param>
        /// <param name="hash">Its content hash.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void LogAdded(int shard, uint id, ulong hash)
        {
            if (Log is { Active: true } log)
                AppendAdded(log, shard, id, hash);
        }

        /// <summary>
        /// The slow path of <see cref="LogAdded"/>: buffers the entry in the shard's list and marks the log pending.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void AppendAdded(ContainerLog log, int shard, uint id, ulong hash)
        {
            var buffers = Volatile.Read(ref logBuffers);
            if (buffers is null)
            {
                // Racing shards agree on one array; each then writes only its own list.
                Interlocked.CompareExchange(ref logBuffers, new List<ContainerLogEntry>?[toId.Length], null);
                buffers = logBuffers!;
            }

            (buffers[shard] ??= new List<ContainerLogEntry>()).Add(new ContainerLogEntry(TypeId, id, hash));
            log.NotePending();
        }

        /// <inheritdoc />
        internal override void DrainLogBuffers(List<ContainerLogEntry> destination)
        {
            for (var i = 0; i < toId.Length; i++)
            {
                List<ContainerLogEntry>? taken;
                lock (toId[i])
                {
                    // Under the lock, as the recording that allocates the array holds one (logBuffers).
                    if (Volatile.Read(ref logBuffers) is not { } buffers)
                        continue;

                    taken = buffers[i];
                    buffers[i] = null;
                }

                if (taken is not null)
                    destination.AddRange(taken);
            }
        }

        /// <inheritdoc />
        internal override void DiscardLogBuffers()
        {
            for (var i = 0; i < toId.Length; i++)
            {
                lock (toId[i])
                {
                    if (Volatile.Read(ref logBuffers) is { } buffers)
                        buffers[i] = null;
                }
            }
        }

        /// <inheritdoc />
        internal override int BufferedLogEntries
        {
            get
            {
                var count = 0;
                for (var i = 0; i < toId.Length; i++)
                    lock (toId[i])
                        count += Volatile.Read(ref logBuffers)?[i]?.Count ?? 0;

                return count;
            }
        }

    }

}
