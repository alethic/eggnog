using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// One level's container log: what the live descendants of a context learn of the containers of classes the level's
    /// environments gain, each through its cursor into the log (<see cref="AncestorCursor"/>). The root's is the
    /// store's (<see cref="Store.ContainerLog"/>), which the root's container environments own
    /// (<see cref="ContainerValues.Log"/>); a child's belongs to its container layers (<see cref="ContainerLayers.Log"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two streams. Added (<see cref="ContainerLogEntry"/>: type, id, content hash) records every container of classes
    /// that appears in one of the level's environments under an id: an intern's miss, a rebuild's fresh insert, and
    /// every rename of an entry (<see cref="ContainerEnv{TContainer}"/>). Dirty records the ids of the containers the
    /// level's container rebuild changed in place (<see cref="AppendDirty"/>), from which each descendant re-stamps the
    /// rows of its own layers that name them, as egglog's <c>refresh_rows_for_values</c> does at the root.
    /// </para>
    /// <para>
    /// Buffers. An environment records an Added entry in a buffer of its own, one per hash-cons shard, under that
    /// shard's lock, which the recording path already holds (interning, and each task of a parallel rebuild, which owns
    /// its shard), and then sets <see cref="Pending"/>, testing it first: no second lock, and no counter shared between
    /// threads. <see cref="Drain"/> moves the buffered entries to the append-only Added list: under the log's lock it
    /// clears <see cref="Pending"/> first, then swaps each buffer out under its shard's lock, so an entry recorded
    /// meanwhile is either drained or sets <see cref="Pending"/> again. A reader drains before it reads, the level drains
    /// at its freeze, and a clear discards.
    /// </para>
    /// <para>
    /// Activation. The log records only while it is active (<see cref="Active"/>), which is while its level has live
    /// descendants: creating a child activates the log of every ancestor that is not frozen before the child takes its
    /// cursors, so they start at the end; disposing a context clears and deactivates the log of every ancestor whose
    /// gate reaches zero, since no cursor into it is left (<see cref="Context.Dispose"/>). A frozen level records
    /// nothing: it interns no container of classes and never rebuilds, so its log keeps what its freeze drained
    /// (<see cref="Freeze"/>). A root with no live descendant pays one volatile read of <see cref="Active"/> where a
    /// container of classes appears.
    /// </para>
    /// <para>
    /// Memory. Nothing is trimmed: a reader's cursor moves past entries without dropping them, and only a clear frees
    /// them. While a level has live descendants, its log therefore holds one 16-byte entry per container of classes
    /// that appeared at the level (an intern, a rebuild's reinsert, a rename) and one id per container its rebuild
    /// changed in place, plus the slack of the growing arrays, however long ago every live cursor passed them.
    /// </para>
    /// <para>
    /// Threads. Every member is thread-safe. The counts are volatile reads; everything else takes the log's private
    /// lock, which readers on several threads (children of a level frozen after they took their cursors) and a drain
    /// share. Lock order: the log's lock, then an environment's shard locks; nothing that holds a shard lock takes the
    /// log's. A cursor past the end, which only a clear can leave and only a disposed descendant can hold, reads nothing
    /// and comes back at the end.
    /// </para>
    /// </remarks>
    internal sealed class ContainerLog
    {

        readonly object sync = new();

        /// <summary>
        /// The environments that record into the log: the root's that hold ids, or a child's own environments. Grown
        /// copy-on-write under <see cref="sync"/>.
        /// </summary>
        ContainerEnvBase[] environments = Array.Empty<ContainerEnvBase>();

        /// <summary>
        /// The drained Added entries; the first <see cref="addedCount"/> are valid. Under <see cref="sync"/>.
        /// </summary>
        ContainerLogEntry[] added = Array.Empty<ContainerLogEntry>();

        /// <summary>
        /// The number of drained Added entries; written under <see cref="sync"/>, read volatile.
        /// </summary>
        int addedCount;

        /// <summary>
        /// The Dirty ids; the first <see cref="dirtyCount"/> are valid. Under <see cref="sync"/>.
        /// </summary>
        uint[] dirty = Array.Empty<uint>();

        /// <summary>
        /// The number of Dirty ids; written under <see cref="sync"/>, read volatile.
        /// </summary>
        int dirtyCount;

        /// <summary>
        /// The Added and Dirty counts the level's freeze left, or -1 before it: a frozen level's log never grows. Under
        /// <see cref="sync"/>.
        /// </summary>
        int frozenAdded = -1;

        int frozenDirty = -1;

        volatile bool active;
        volatile bool pending;

        /// <summary>
        /// Gets whether the log records: its level has live descendants. One volatile read.
        /// </summary>
        public bool Active => active;

        /// <summary>
        /// Gets whether an environment may have buffered Added entries since the last drain: set by every recording, so
        /// a reader that finds it clear and the count at its cursor has nothing new to read. One volatile read.
        /// </summary>
        public bool Pending => pending;

        /// <summary>
        /// Gets the number of drained Added entries: what a cursor taken now starts at, after a drain. One volatile read.
        /// </summary>
        public int AddedCount => Volatile.Read(ref addedCount);

        /// <summary>
        /// Gets the number of Dirty ids. One volatile read.
        /// </summary>
        public int DirtyCount => Volatile.Read(ref dirtyCount);

        /// <summary>
        /// Gets whether Added entries lie past <paramref name="cursor"/>, drained or still buffered. Two volatile reads.
        /// </summary>
        /// <param name="cursor">A reader's Added cursor.</param>
        /// <returns><c>true</c> if a read from the cursor, after a drain, may find an entry.</returns>
        public bool HasAddedSince(int cursor) => pending || Volatile.Read(ref addedCount) > cursor;

        /// <summary>
        /// Gives <paramref name="environment"/> this log, which it records its Added entries into from then on, and
        /// includes it in every drain and clear. Called before the environment is published or holds ids
        /// (<see cref="ContainerValues.RegisterType{TContainer}"/>, <see cref="ContainerEnvBase.MarkHoldsIds"/>,
        /// <see cref="ContainerLayers.GetOrCreateLayer{TContainer}"/>), so every thread that records into it sees the log.
        /// Attaching it again does nothing.
        /// </summary>
        /// <param name="environment">The environment.</param>
        public void Attach(ContainerEnvBase environment)
        {
            ArgumentNullException.ThrowIfNull(environment);
            lock (sync)
            {
                if (Array.IndexOf(environments, environment) >= 0)
                    return;

                var grown = new ContainerEnvBase[environments.Length + 1];
                Array.Copy(environments, grown, environments.Length);
                grown[^1] = environment;
                environments = grown;
                environment.Log = this;
            }
        }

        /// <summary>
        /// Takes <paramref name="environment"/> out of the drains and clears again: an environment that was attached and
        /// never published, the losing copy of a race to create a layer.
        /// </summary>
        /// <param name="environment">The environment.</param>
        public void Detach(ContainerEnvBase environment)
        {
            ArgumentNullException.ThrowIfNull(environment);
            lock (sync)
            {
                var index = Array.IndexOf(environments, environment);
                if (index < 0)
                    return;

                var shrunk = new ContainerEnvBase[environments.Length - 1];
                Array.Copy(environments, shrunk, index);
                Array.Copy(environments, index + 1, shrunk, index, shrunk.Length - index);
                environments = shrunk;
            }
        }

        /// <summary>
        /// Starts recording: a child is about to be created below the level. Called on the level's domain thread by
        /// <see cref="Context.CreateChild"/> for every ancestor of the new child that is not frozen; nothing when the log
        /// is active already.
        /// </summary>
        public void Activate()
        {
            lock (sync)
            {
                Debug.Assert(frozenAdded < 0, "a frozen level's container log was activated");
                active = true;
            }
        }

        /// <summary>
        /// Marks that an environment buffered an Added entry: one volatile read, and a write only when the flag was
        /// clear. Called by the recording environment under the shard lock it holds, after the entry is buffered.
        /// </summary>
        public void NotePending()
        {
            if (pending == false)
                pending = true;
        }

        /// <summary>
        /// Moves every buffered Added entry to the Added list. Nothing when <see cref="Pending"/> is clear: a recording
        /// that happened before the call set it. Cost: one lock and, when something is pending, one shard lock per shard
        /// of each environment, plus the entries moved.
        /// </summary>
        public void Drain()
        {
            if (pending == false)
                return;

            lock (sync)
            {
                if (pending == false)
                    return;

                // Cleared first: an entry buffered after its shard is swapped out sets the flag again.
                pending = false;
                var drained = new List<ContainerLogEntry>();
                foreach (var environment in environments)
                    environment.DrainLogBuffers(drained);

                if (drained.Count == 0)
                    return;

                var count = addedCount;
                if (count + drained.Count > added.Length)
                    Array.Resize(ref added, Math.Max(count + drained.Count, Math.Max(16, added.Length * 2)));

                drained.CopyTo(added, count);
                Volatile.Write(ref addedCount, count + drained.Count);
            }
        }

        /// <summary>
        /// Appends the Added entries from <paramref name="cursor"/> to the end to <paramref name="destination"/>, when it
        /// is given, and returns the end: the reader's new cursor. Reads the Added list only: drain first. A cursor past
        /// the end reads nothing and comes back at the end.
        /// </summary>
        /// <param name="cursor">The reader's Added cursor.</param>
        /// <param name="destination">Receives the entries, in the order drained; <c>null</c> to skip them.</param>
        /// <returns>The number of Added entries.</returns>
        public int ReadAdded(int cursor, List<ContainerLogEntry>? destination)
        {
            lock (sync)
            {
                if (destination is not null && cursor < addedCount)
                    destination.AddRange(new ReadOnlySpan<ContainerLogEntry>(added, cursor, addedCount - cursor));

                return addedCount;
            }
        }

        /// <summary>
        /// Appends <paramref name="ids"/> to the Dirty stream, while the log is active; nothing otherwise. Called on the
        /// level's domain thread at the end of its container rebuild. O(ids) amortized.
        /// </summary>
        /// <param name="ids">The ids of the containers rebuilt in place.</param>
        public void AppendDirty(ReadOnlySpan<uint> ids)
        {
            if (ids.IsEmpty)
                return;

            lock (sync)
            {
                if (active == false)
                    return;

                Debug.Assert(frozenDirty < 0, "a frozen level's container log grew");
                var count = dirtyCount;
                if (count + ids.Length > dirty.Length)
                    Array.Resize(ref dirty, Math.Max(count + ids.Length, Math.Max(16, dirty.Length * 2)));

                ids.CopyTo(dirty.AsSpan(count));
                Volatile.Write(ref dirtyCount, count + ids.Length);
            }
        }

        /// <summary>
        /// Returns the Dirty ids from <paramref name="cursor"/> to the end, and the end: the reader's new cursor. A cursor
        /// past the end reads nothing and comes back at the end.
        /// </summary>
        /// <param name="cursor">The reader's Dirty cursor.</param>
        /// <param name="end">The number of Dirty ids.</param>
        /// <returns>A copy of the ids; empty when there are none.</returns>
        public uint[] ReadDirty(int cursor, out int end)
        {
            lock (sync)
            {
                end = dirtyCount;
                return cursor < dirtyCount ? dirty.AsSpan(cursor, dirtyCount - cursor).ToArray() : Array.Empty<uint>();
            }
        }

        /// <summary>
        /// Drains the log as part of its level's freeze and stamps its counts, which never grow again: the descendants
        /// that existed before the freeze read the drained tail, and then settle the level. Domain thread only.
        /// </summary>
        public void Freeze()
        {
            Drain();
            lock (sync)
            {
                frozenAdded = addedCount;
                frozenDirty = dirtyCount;
            }
        }

        /// <summary>
        /// Deactivates the log and discards what it holds, buffered entries included: its level's gate reached zero, so
        /// no cursor into it is left. Called by <see cref="Context.Dispose"/> for every ancestor whose gate it brought
        /// to zero, from whichever thread disposes the last descendant; by <see cref="Context.CreateChild"/> for every
        /// ancestor it activated whose gate counts no descendant, when the child's construction throws; and by
        /// <see cref="Release"/>. Nothing records meanwhile, since the level is idle or frozen.
        /// </summary>
        public void Clear()
        {
            lock (sync)
            {
                // Deactivated first: a recording that takes its shard lock after the shard is discarded sees it.
                active = false;
                foreach (var environment in environments)
                    environment.DiscardLogBuffers();

                pending = false;
                added = Array.Empty<ContainerLogEntry>();
                Volatile.Write(ref addedCount, 0);
                dirty = Array.Empty<uint>();
                Volatile.Write(ref dirtyCount, 0);
            }
        }

        /// <summary>
        /// Clears the log (<see cref="Clear"/>) and lets go of every environment attached to it: its level, a child
        /// context, is disposed, and its container layers drop their own environments
        /// (<see cref="ContainerLayers.Release"/>), which the log must not keep reachable for as long as the disposed
        /// context is.
        /// </summary>
        public void Release()
        {
            lock (sync)
            {
                Clear();
                environments = Array.Empty<ContainerEnvBase>();
            }
        }

        /// <summary>
        /// Adds to <paramref name="violations"/> every way the log breaks its invariants, each starting with
        /// <c>container log:</c>: an inactive log holds nothing, buffered or drained; a frozen level's log holds nothing
        /// buffered and no more than its freeze left; and every environment it drains records into it.
        /// </summary>
        /// <param name="depth">The level's depth, for the messages.</param>
        /// <param name="violations">Receives one line per violation.</param>
        public void CheckInvariants(int depth, List<string> violations)
        {
            ArgumentNullException.ThrowIfNull(violations);
            lock (sync)
            {
                var buffered = 0;
                foreach (var environment in environments)
                {
                    buffered += environment.BufferedLogEntries;
                    if (ReferenceEquals(environment.Log, this) == false)
                        Context.Report(violations, $"container log: an environment of type {environment.TypeId} that the log at depth {depth} drains records into another log");
                }

                if (active == false && (addedCount > 0 || dirtyCount > 0 || pending || buffered > 0))
                    Context.Report(violations, $"container log: the log at depth {depth} is inactive but holds {addedCount} Added entries, {dirtyCount} Dirty ids and {buffered} buffered entries{(pending ? ", pending" : "")}");

                if (frozenAdded >= 0 && (addedCount > frozenAdded || dirtyCount > frozenDirty || buffered > 0))
                    Context.Report(violations, $"container log: the log at depth {depth} held {frozenAdded} Added entries and {frozenDirty} Dirty ids when its level froze, and holds {addedCount}, {dirtyCount} and {buffered} buffered now");
            }
        }

    }

}
