using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class ContextTables
    {

        /// <summary>
        /// The cursor into each ancestor, by depth; only depths in <c>[settled, Depth)</c> have one.
        /// </summary>
        readonly AncestorCursor?[] cursors;

        /// <summary>
        /// The layer versions of each ancestor that was not settled when the context was created, by depth then table
        /// index: where its view started. Rows an ancestor appended at or beyond them came after the context's
        /// creation; the invariant checker reads them. <c>null</c> for the ancestors settled at creation, which never
        /// append.
        /// </summary>
        readonly TableVersion[]?[] creationRows;

        /// <summary>
        /// The number of leading ancestors the context has read in full: frozen ancestors that cannot change any more,
        /// whose cursors are dropped.
        /// </summary>
        int settled;

        /// <summary>
        /// The number of leading ancestors that were settled when the context was created.
        /// </summary>
        int settledAtCreation;

        /// <summary>
        /// Gets whether some ancestor that is not settled changed since the context last caught up with it: its change
        /// stamp moved (the root's is its database's <see cref="Database.ChangeStamp"/>); its container log holds Dirty
        /// ids past the context's cursor, the ids of containers it rebuilt in place, whose new meaning the context's
        /// rows naming them must show (<see cref="ReadDirty"/>), which may come without a displacement or a row
        /// change, when the ancestor's catch-up claimed and rebuilt a container its own ancestor gained; or, while the
        /// context has a displacement or a container layer, its container log holds Added entries past the context's
        /// cursor, drained or still pending (<see cref="ContainerLog.HasAddedSince"/>), which the pre-loop scan of the
        /// context's rebuild has not read (<see cref="ContainerLayers.ScanAdds"/>). A context with neither learns of
        /// such a container only through a later displacement of its own, whose container pass claims every container
        /// that holds it (<see cref="ContainerLayers.RebuildPass"/>). O(unsettled ancestors), three volatile reads
        /// each.
        /// </summary>
        public bool NeedsCatchUp
        {
            get
            {
                if (settled == depth)
                    return false;

                var containers = owner.Journal is { Count: > 0 } || owner.ContainerLayers is { IsEmpty: false };
                for (var a = settled; a < depth; a++)
                {
                    var cursor = cursors[a]!;
                    if (chain[a].Database.ChangeStamp != cursor.Stamp)
                        return true;

                    var log = chain[a].ContainerLog;
                    if (log.DirtyCount > cursor.Dirty)
                        return true;

                    if (containers && log.HasAddedSince(cursor.Added))
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Gets whether the container log of some ancestor that is not settled holds Added entries past the context's
        /// cursor into it, drained or still pending: what <see cref="ReadAdded"/> would read, and the pre-loop scan of
        /// the context's rebuild then reads (<see cref="ContainerLayers.ScanAdds"/>). O(unsettled ancestors), two
        /// volatile reads each.
        /// </summary>
        internal bool HasNewAddedEntries
        {
            get
            {
                for (var a = settled; a < depth; a++)
                    if (chain[a].ContainerLog.HasAddedSince(cursors[a]!.Added))
                        return true;

                return false;
            }
        }

        /// <summary>
        /// Reads the Added entries the container log of every ancestor that is not settled holds past the context's
        /// cursor into it, draining the log first, and appends each to <paramref name="entries"/> with the depth of its
        /// ancestor; returns the logs' ends, which <see cref="CommitAdded"/> makes the cursors' positions: the pre-loop
        /// scan of the context's rebuild (<see cref="ContainerLayers.ScanAdds"/>). Domain thread only; it changes
        /// nothing a reader of the context sees, so it may run inside a callback.
        /// </summary>
        /// <param name="entries">Receives the new entries, ancestor by ancestor in the order drained.</param>
        /// <returns>The end of each ancestor's Added stream by depth, or -1 where nothing was new.</returns>
        internal int[] ReadAdded(List<(int Depth, ContainerLogEntry Entry)> entries)
        {
            var ends = new int[depth];
            var read = new List<ContainerLogEntry>();
            for (var a = 0; a < depth; a++)
            {
                ends[a] = -1;
                if (a < settled)
                    continue;

                var cursor = cursors[a]!;
                var log = chain[a].ContainerLog;
                if (log.HasAddedSince(cursor.Added) == false)
                    continue;

                log.Drain();
                read.Clear();
                ends[a] = log.ReadAdded(cursor.Added, read);
                foreach (var entry in read)
                    entries.Add((a, entry));
            }

            return ends;
        }

        /// <summary>
        /// Moves the context's Added cursor into the container log of every ancestor that is not settled to the log's
        /// end, draining the log first, without reading the entries it moves past: the pre-loop scan of a context that
        /// none of them can concern (<see cref="ContainerLayers.ScanAdds"/>). Committed at once. It allocates nothing
        /// beyond the drains, which move the entries the ancestors' environments buffered into their logs
        /// (<see cref="ContainerLog.Drain"/>). Domain thread only; it changes nothing a reader of the context sees, so
        /// it may run inside a callback.
        /// </summary>
        internal void SkipAdded()
        {
            for (var a = settled; a < depth; a++)
            {
                var cursor = cursors[a]!;
                var log = chain[a].ContainerLog;
                if (log.HasAddedSince(cursor.Added) == false)
                    continue;

                log.Drain();
                cursor.Added = log.ReadAdded(cursor.Added, null);
            }
        }

        /// <summary>
        /// Moves the context's Added cursor into the container log of every ancestor that is not settled to the end
        /// <see cref="ReadAdded"/> returned for it. Committed at once, before the rebuild loop, since what the pre-loop
        /// scan consumed stays consumed and what it kept waits in the context's container layers
        /// (<see cref="ContainerLayers.HasPending"/>), so a scan that keeps nothing leaves the context clean without a
        /// pass. Domain thread only.
        /// </summary>
        /// <param name="ends">The ends, by depth; -1 leaves a cursor where it is.</param>
        internal void CommitAdded(int[] ends)
        {
            for (var a = settled; a < depth; a++)
                if (ends[a] >= 0)
                    cursors[a]!.Added = ends[a];
        }

        /// <summary>
        /// Returns the Dirty ids the container logs of the ancestors that are not settled gained since the context's
        /// cursors, and records each log's end as pending, which <see cref="CommitCursors"/> publishes after the
        /// rebuild loop: the ids of containers an ancestor's rebuild changed in place, whose new meaning the rows of
        /// the context's own layers that name them must show, closed by the context's container pass over its view
        /// (<see cref="Context.RebuildContainerLayers"/>). An id that a level above its ancestor, up to the context,
        /// shadows is left out: the context sees that level's container under it, not the ancestor's. Run by the first
        /// pass of <see cref="Context.Rebuild"/>; O(unsettled ancestors) plus the ids read, each tested against the
        /// shadowed ids of the levels above its ancestor.
        /// </summary>
        /// <returns>The ids, ancestor by ancestor; empty when there are none.</returns>
        internal uint[] ReadDirty()
        {
            List<uint>? result = null;
            var layers = owner.ContainerLayers!;
            for (var a = settled; a < depth; a++)
            {
                var cursor = cursors[a]!;
                var ids = chain[a].ContainerLog.ReadDirty(cursor.Dirty, out var end);
                cursor.SetPendingDirty(end);
                foreach (var id in ids)
                    if (layers.ShadowedAboveDepth(a, id) == false)
                        (result ??= new List<uint>()).Add(id);
            }

            return result?.ToArray() ?? Array.Empty<uint>();
        }

        /// <summary>
        /// Gets the number of leading ancestors the context has read in full (frozen, cursors dropped).
        /// </summary>
        internal int SettledDepth => settled;

        /// <summary>
        /// Gets the cursor into the ancestor at depth <paramref name="depthOfAncestor"/>, or <c>null</c> when it is settled.
        /// </summary>
        /// <param name="depthOfAncestor">An ancestor depth.</param>
        /// <returns>The cursor, or <c>null</c>.</returns>
        internal AncestorCursor? CursorAt(int depthOfAncestor) => cursors[depthOfAncestor];

        /// <summary>
        /// The second step of a child's catch-up, after the union-find's: reads every row the unsettled ancestors
        /// appended to a shared table since the context last caught up with them, and copies into the context, at
        /// <paramref name="epoch"/>, each one the context must hold itself; the copied ancestor rows are hidden once
        /// their copies are staged.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Run by the first pass of <see cref="Context.Rebuild"/>, after the union-find caught up, while every ancestor
        /// is idle: frozen, or rebuilt and sharing the context's thread domain. For each live appended row that the
        /// context sees (hidden at no level between the row's layer and the context, and not below a level that cleared
        /// the table):
        /// </para>
        /// <list type="bullet">
        /// <item><description>(a) it names an id that is not canonical in the context: copied canonically
        /// (<see cref="HideReason.Mentions"/>). This also covers an ancestor row that was stale in its own layer when a
        /// raw root write appended it, since <c>find_C = overlay ∘ find_P</c>;</description></item>
        /// <item><description>(b) the context's own layer holds its key: copied as it is
        /// (<see cref="HideReason.KeyHeld"/>), so the next merge folds it into the own row and the context sees one row
        /// with the key;</description></item>
        /// <item><description>(c) its timestamp lies below the context's semi-naive floor
        /// (<see cref="Context.SemiNaiveFloor"/>): copied to re-stamp it (<see cref="HideReason.Restamped"/>), so a
        /// watermark the context already used still sees it as new (a safeguard of the watermarks; it never fires while
        /// writers stamp with their state's timestamp);</description></item>
        /// <item><description>(d) otherwise it is visible as it is: canonical in the context, and no own row holds its
        /// key.</description></item>
        /// </list>
        /// <para>
        /// A visible contradiction row stops a context created with <see cref="ContextOptions.StopOnContradiction"/>
        /// the copies staged so far are then dropped unflushed, and the context answers only
        /// <see cref="Context.IsContradictory"/>. Ancestor tombstones need no work: a replaced row reappears as an
        /// append. Before reading, every layer the cursors have seen is checked to be in the same generation: a layer
        /// that compacted or was cleared under a live descendant would renumber the rows the cursors and hidden sets
        /// name, so the catch-up throws, which faults the context.
        /// </para>
        /// <para>
        /// What was read is recorded in the cursors as pending, and <see cref="CommitCursors"/> publishes it after the
        /// rebuild loop. Cost: O(rows appended to unsettled ancestors since the last catch-up) plus O(tables × unsettled
        /// depth); an ancestor whose change stamp did not move is skipped, so nothing under frozen ancestors once read.
        /// </para>
        /// </remarks>
        /// <param name="epoch">The pass's epoch, which the copies are stamped with.</param>
        /// <returns>Whether any ancestor row was copied and hidden.</returns>
        /// <exception cref="InvalidOperationException">An ancestor layer changed generation, or a copy would create the
        /// layer of a table whose merge is not a lattice merge, which only the root may write.</exception>
        internal bool CatchUpRows(uint epoch)
        {
            if (settled == depth)
                return false;

            var tables = schema.FunctionTables;
            CheckGenerations(tables);
            ExecutionState? state = null;
            PendingHides();
            var floor = owner.SemiNaiveFloor;
            for (var a = settled; a < depth; a++)
            {
                var cursor = cursors[a]!;
                var stamp = chain[a].Database.ChangeStamp;
                if (stamp == cursor.Stamp)
                    continue;

                foreach (var table in tables)
                {
                    if (schema[table] is not { IsContextPrivate: false } s || AncestorLayer(a, table) is not { } layer)
                        continue;

                    var version = layer.Version;
                    var from = cursor.RowsOrDefault(table).Minor;
                    cursor.SetPendingRows(table, version);
                    if (from >= version.Minor || a < ClearedFloor(table))
                        continue;

                    if (CatchUpLayer(ref state, s, layer, a, from, version.Minor, floor, epoch))
                        return true;
                }

                cursor.SetPendingStamp(stamp);
            }

            if (state is not null)
            {
                state.Flush();
                owner.Database.RecordFailure(state);
            }

            return ApplyHides();
        }

        /// <summary>
        /// Moves every cursor to what the last catch-up read, after the rebuild loop, while no ancestor can change
        /// (they are frozen or share the domain); a frozen ancestor read in full becomes settled and drops its cursor,
        /// its container log's positions with it: the ancestor's freeze drained its log, which never grows again, and
        /// the rebuild read it to the end (the pre-loop scan, <see cref="ContainerLayers.ScanAdds"/>, and the first
        /// pass's Dirty read).
        /// </summary>
        /// <returns>Whether any cursor moved: the context's views must then be rebuilt, since an ancestor's hides may have
        /// changed even where none of its layers grew.</returns>
        internal bool CommitCursors()
        {
            var moved = false;
            for (var a = settled; a < depth; a++)
                moved |= cursors[a]!.CommitPending();

            while (settled < depth && chain[settled].IsFrozen)
                cursors[settled++] = null;

            return moved;
        }

        /// <summary>
        /// Reads the rows <c>[from, to)</c> of the ancestor layer at depth <paramref name="a"/>; see
        /// <see cref="CatchUpRows"/>.
        /// </summary>
        /// <returns><c>true</c> if a contradiction stopped the context, which ends the catch-up.</returns>
        bool CatchUpLayer(ref ExecutionState? state, LayeredTableSchema s, SortedWritesTable layer, int a, int from, int to, uint floor, uint epoch)
        {
            var table = s.Id;
            var find = owner.UnionFind;
            var columns = s.RebuildColumns;
            var sortColumn = s.SortColumn;
            var arity = layer.Arity;

            // The ancestor is idle: staging copies into the own layer never moves its rows.
            var data = layer.Rows.Data;
            for (var r = from; r < to; r++)
            {
                statistics.CatchUpRowsExamined++;
                var offset = r * arity;
                if (data[offset] == Value.StaleRep || HiddenBetween(table, a, r))
                    continue;

                if (s.IsContradiction)
                {
                    SetContradicted();
                    if (owner.IsContradicted)
                        return true;
                }

                var stale = false;
                foreach (var column in columns)
                {
                    var id = data[offset + column.Rep];
                    if (find.Find(id) != id)
                    {
                        stale = true;
                        break;
                    }
                }

                if (stale)
                {
                    StageCopy(ref state, s, layer, a, r, HideReason.Mentions, true, epoch);
                }
                else if (TryGetLayer(table)?.Own is { } own && own.FindRow(data, offset) >= 0)
                {
                    StageCopy(ref state, s, layer, a, r, HideReason.KeyHeld, false, epoch);
                }
                else if (sortColumn >= 0 && data[offset + sortColumn] < floor)
                {
                    StageCopy(ref state, s, layer, a, r, HideReason.Restamped, false, epoch);
                    statistics.RestampedCopies++;
#if DEBUG
                    // The re-stamp stays as defence in depth, but every engine and bridge path stamps with the state's
                    // timestamp, so only the raw-write contract can put a row below a reader's floor.
                    Debug.Assert(owner.Store.HasRawWrites, $"An ancestor row of table {table} was stamped {data[offset + sortColumn]}, below this context's semi-naive floor {floor}, without raw writes (a re-stamped catch-up copy).");
#endif
                }
            }

            return false;
        }

        /// <summary>
        /// Throws when a layer some unsettled cursor has seen is no longer in the generation the cursor saw. Only
        /// ancestors whose change stamp moved are checked: a layer changes generation only in a merge that changed
        /// rows, a clear, or a compaction, each of which moves its context's stamp.
        /// </summary>
        void CheckGenerations(ReadOnlySpan<TableId> tables)
        {
            for (var a = settled; a < depth; a++)
            {
                var cursor = cursors[a]!;
                if (chain[a].Database.ChangeStamp == cursor.Stamp)
                    continue;

                foreach (var table in tables)
                {
                    // A context-private table is never read by descendants and may be retracted or cleared freely.
                    var seen = cursor.RowsOrDefault(table);
                    if (seen == default || schema[table] is not { IsContextPrivate: false })
                        continue;

                    var major = AncestorLayer(a, table)?.Version.Major ?? 0;
                    if (major != seen.Major)
                        ThrowGenerationChanged(table, a, seen.Major, major);
                }
            }
        }

        /// <summary>
        /// Takes the creation cursors: the leading frozen ancestors are settled (the context starts from their final
        /// state), and every other ancestor gets a cursor at its current layer versions and change stamp, and at the end
        /// of its container log, drained first, which <see cref="Context.CreateChild"/> activated. Called by the
        /// constructor, before <see cref="Context.CreatedAt"/> is read.
        /// </summary>
        void InitializeCursors()
        {
            while (settled < depth && chain[settled].IsFrozen)
                settled++;

            settledAtCreation = settled;
            var count = schema.TableCount;
            for (var a = settled; a < depth; a++)
            {
                var rows = new TableVersion[count];
                foreach (var table in schema.FunctionTables)
                    if (table.Index < count && schema[table] is { IsContextPrivate: false })
                        rows[table.Index] = AncestorLayer(a, table)?.Version ?? default;

                // The cursor replaces its array when it commits and never writes this one, so it can be shared. The log
                // is drained first, so what the ancestor buffered before the context existed counts as read.
                var log = chain[a].ContainerLog;
                log.Drain();
                cursors[a] = new AncestorCursor(rows, chain[a].Database.ChangeStamp, log.AddedCount, log.DirtyCount);
                creationRows[a] = rows;
            }
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowGenerationChanged(TableId table, int depth, int seen, int now)
        {
            throw new InvalidOperationException($"The layer of table {table} at depth {depth} moved from generation {seen} to {now} while a descendant held cursors and hidden sets over its rows; compaction and clears are gated while descendants live.");
        }

    }

}
