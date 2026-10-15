using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// The member map last built for this context (<see cref="GetMemberMap"/>), valid while its overlay's version is
        /// unchanged; <c>null</c> until the first call.
        /// </summary>
        MemberMap? memberMap;

        /// <summary>
        /// The split records opened in this context as a scope, in creation order; <c>null</c> until the first. Guarded
        /// by itself once created.
        /// </summary>
        List<SplitRecord>? splitRecords;

        /// <summary>
        /// Visits every live row this child stored itself in <paramref name="function"/>'s table: its own layer's rows,
        /// canonical in it; nothing when it has no layer of the table. The lift's first source of candidate keys.
        /// </summary>
        /// <remarks>
        /// Requires the context to be clean (<see cref="IsClean"/>). The sink runs as a callback of the context's
        /// thread domain: it may read any context, and writes and structural calls are refused there. Each row is the
        /// table's full row: the keys, the output, the timestamp and, for a function that can subsume, the subsumption
        /// flag. O(own rows).
        /// </remarks>
        /// <typeparam name="TSink">The sink type; a struct, so the scan is specialized for it.</typeparam>
        /// <param name="function">The function.</param>
        /// <param name="sink">Receives each row with its row id in the own layer.</param>
        /// <exception cref="ArgumentException">The function is not declared.</exception>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is the root; or it is faulted, stopped or not clean, or
        /// its domain is held by another thread.</exception>
        internal void ScanOwn<TSink>(FunctionId function, ref TSink sink)
            where TSink : struct, IRowSink
        {
            var table = owner.GetTable(function);
            var current = context.IsFrozen ? null : context.Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            context.ScanOwn(table, ref sink);
        }

        /// <summary>
        /// Visits every ancestor row of <paramref name="function"/>'s table this child sees that names
        /// <paramref name="id"/> in an id column: the lift's winner probe, its second source of candidate keys, through
        /// the ancestors' rebuild indexes, never a scan.
        /// </summary>
        /// <remarks>
        /// Nothing for a table without id columns, for a context-private table and for an id local to the child.
        /// <paramref name="id"/> must be canonical in the child. Requires the context to be clean; the sink runs as a
        /// callback, as in <see cref="ScanOwn{TSink}"/>. Rows come layer by layer from the root up. Cost: O(depth) index
        /// probes plus the uses of the id.
        /// </remarks>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="function">The function.</param>
        /// <param name="id">An id canonical in the child.</param>
        /// <param name="sink">Receives each row with its row id in its layer.</param>
        /// <exception cref="ArgumentException">The function is not declared.</exception>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is the root; or it is faulted, stopped or not clean, or
        /// its domain is held by another thread.</exception>
        internal void ScanAncestorsMentioning<TSink>(FunctionId function, uint id, ref TSink sink)
            where TSink : struct, IRowSink
        {
            var table = owner.GetTable(function);
            var current = context.IsFrozen ? null : context.Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            context.ScanAncestorsMentioning(table, id, ref sink);
        }

        /// <summary>
        /// Visits the merges this child made over its parent's ids: for every class of its overlay, each member that is
        /// not local to the child, paired with the class's canonical id; a class's canonical is visited paired with
        /// itself when it is a parent id. Slot order, then insertion order.
        /// </summary>
        /// <remarks>
        /// Requires the context to be clean. The visitor runs as a callback of the context's thread domain.
        /// O(overlay members).
        /// </remarks>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="visitor">The visitor; its final state is written back.</param>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is the root; or it is faulted, stopped or not clean, or
        /// its domain is held by another thread.</exception>
        internal void ScanMerges<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IMergeVisitor
        {
            var current = context.IsFrozen ? null : context.Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            context.ScanMerges(ref visitor);
        }

        /// <summary>
        /// Gets whether <paramref name="id"/> was minted in this child: it lies in one of the child's id blocks. Every
        /// term mint in a child is local; container ids are minted on the store's shared counter and never are.
        /// Lock-free, O(log blocks).
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if the id is local to this context.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is the root, which has no local ids.</exception>
        public bool IsLocalId(Value id) => context.IsLocalId(id.Rep);

        /// <summary>
        /// Gets the child's member map: for every class it merged over its parent's ids, the members that are not
        /// local, ascending. Cached while the overlay's version is unchanged, so repeated lifts over an unchanged case
        /// cost one comparison.
        /// </summary>
        /// <remarks>
        /// Requires the context to be clean. A rebuild is O(overlay members × log). Thread-safe: on a frozen context two
        /// threads may build equal maps, and either is kept.
        /// </remarks>
        /// <returns>The map.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is the root; or it is faulted, stopped or not clean, or
        /// its domain is held by another thread.</exception>
        public MemberMap GetMemberMap()
        {
            var current = context.IsFrozen ? null : context.Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            var overlay = context.Overlay ?? throw new InvalidOperationException("The root context merged nothing over a parent; it has no member map.");
            var cached = Volatile.Read(ref memberMap);
            if (cached is not null && cached.Version == overlay.Version)
                return cached;

            var built = MemberMap.Build(overlay, context.IdBlocks!);
            Volatile.Write(ref memberMap, built);
            return built;
        }

        /// <summary>
        /// Builds the child's member map afresh, bypassing the cache (<see cref="MemberMap.Build(EGraphContext)"/>; the
        /// member-map check of <see cref="CheckInvariants"/> compares it with the cached one). Same requirements as
        /// <see cref="GetMemberMap"/>.
        /// </summary>
        /// <returns>A new map.</returns>
        internal MemberMap BuildMemberMap()
        {
            var current = context.IsFrozen ? null : context.Domain;
            using var scope = current is null ? default : current.Enter();
            ThrowIfNotReadable();
            var overlay = context.Overlay ?? throw new InvalidOperationException("The root context merged nothing over a parent; it has no member map.");
            return MemberMap.Build(overlay, context.IdBlocks!);
        }

        /// <summary>
        /// Gets the member map cached by <see cref="GetMemberMap"/>, current or not, without building one; <c>null</c>
        /// before the first call. For the member-map check of <see cref="CheckInvariants"/>.
        /// </summary>
        internal MemberMap? CachedMemberMap => Volatile.Read(ref memberMap);

        /// <summary>
        /// Gets this context's find for a lift or a meet, after checking once that the context may be read: the caller
        /// holds the context's domain (or the context is frozen) and reads nothing that a write could change before it is
        /// done.
        /// </summary>
        /// <returns>The context's layered find.</returns>
        /// <exception cref="InvalidOperationException">The context may not be read.</exception>
        internal LayeredUnionFind LiftFinder()
        {
            ThrowIfNotReadable();
            return context.UnionFind;
        }

        /// <summary>
        /// Looks up the output of <paramref name="info"/>'s function for <paramref name="key"/> in this context's view, the
        /// key's id cells canonicalized here first: the body of <see cref="TryLookup"/> without its checks, for the lift,
        /// which checks readability once per round (<see cref="LiftFinder"/>) and holds the domain. O(id cells × depth)
        /// finds plus one probe per layer; no allocation for keys of up to 32 cells.
        /// </summary>
        /// <param name="info">The function.</param>
        /// <param name="key">One cell per key column.</param>
        /// <param name="value">The output; 0 when not found.</param>
        /// <returns><c>true</c> if the context sees a row for the key.</returns>
        internal bool LiftLookup(FunctionInfo info, ReadOnlySpan<uint> key, out uint value)
        {
            Span<uint> canonical = key.Length <= MaxStackRow ? stackalloc uint[MaxStackRow] : new uint[key.Length];
            canonical = canonical.Slice(0, key.Length);
            var schema = info.Schema;
            var finder = context.UnionFind;
            for (var i = 0; i < key.Length; i++)
                canonical[i] = schema[i].IsId ? finder.Find(key[i]) : key[i];

            var output = info.Math.OutputColumn;
            if (parent is null)
                return context.Database.GetTable(info.Table).TryGetRowColumn(canonical, new ColumnId(output), out value);

            return context.Tables!.GetView(info.Table).TryGetColumn(canonical, output, out value);
        }

        /// <summary>
        /// Gets the output cell of a live row this context stored itself in <paramref name="table"/>, read past the
        /// stop flag: what a stopped case merged into the contradiction table, which the lift copies when every case is
        /// vacuous. O(own rows of the table) until the first live row.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="column">The column to read.</param>
        /// <param name="value">The cell.</param>
        /// <returns><c>true</c> if the context's own layer holds a live row.</returns>
        internal bool TryReadOwnCell(TableId table, int column, out uint value)
        {
            value = 0;
            if (context.IsDisposed || context.Tables?.TryGetLayer(table)?.Own is not { } own)
                return false;

            var rows = own.Rows;
            for (var r = 0; r < rows.Count; r++)
            {
                if (rows.IsStale(r))
                    continue;

                value = rows.GetRow(r)[column];
                return true;
            }

            return false;
        }

        /// <summary>
        /// The member-map part of <see cref="CheckInvariants"/>: in a child, a member map built now holds exactly the
        /// overlay's members that are not local, each in its group root's overlay class, canonical in the parent and
        /// mapped by this context's find to its group's root, with roots and members ascending; the cached map, when its
        /// version is current, equals it, and one that does not is checked the same way, so that its defects are named.
        /// Nothing at the root. O(overlay members × depth).
        /// </summary>
        /// <param name="violations">Receives each violation.</param>
        internal void CheckMemberMaps(List<string> violations)
        {
            if (context.Overlay is not { } overlay || parent is null)
                return;

            var map = MemberMap.Build(overlay, context.IdBlocks!);
            CheckMemberMap(overlay, map, "member maps: ", violations);
            if (Volatile.Read(ref memberMap) is { } cached && cached.Version == overlay.Version && SameMap(cached, map) == false)
            {
                Context.Report(violations, "member maps: the cached member map is current by version but differs from the overlay");
                CheckMemberMap(overlay, cached, "member maps: the cached map: ", violations);
            }
        }

        /// <summary>
        /// Checks one member map of this child against its overlay: as many members as the overlay holds that are not
        /// local; roots ascending and canonical in the context, each in an overlay class; and each group's members
        /// ascending, not local, members of the root's overlay class, canonical in the parent and found to the root
        /// here. With the count and the order, membership makes the map's members exactly the overlay's that are not
        /// local. O(overlay members × depth).
        /// </summary>
        /// <param name="overlay">The child's overlay.</param>
        /// <param name="map">The map to check.</param>
        /// <param name="prefix">Starts every violation reported.</param>
        /// <param name="violations">Receives each violation.</param>
        void CheckMemberMap(UnionFindOverlay overlay, MemberMap map, string prefix, List<string> violations)
        {
            var blocks = context.IdBlocks!;
            var finder = context.UnionFind;
            var parentFinder = parent!.context.UnionFind;
            var expected = 0;
            for (var slot = 0; slot < overlay.SlotCount; slot++)
            {
                if (overlay.CountOf(slot) < 2)
                    continue;

                foreach (var m in overlay.MembersOf(slot))
                    if (blocks.Contains(m) == false)
                        expected++;
            }

            if (map.MemberCount != expected)
                Context.Report(violations, $"{prefix}the member map holds {map.MemberCount} member(s), the overlay {expected} that are not local");

            var roots = map.GroupRoots;
            for (var g = 0; g < roots.Length; g++)
            {
                var root = roots[g];
                if (g > 0 && roots[g - 1] >= root)
                    Context.Report(violations, $"{prefix}group roots are not ascending at {root}");

                if (finder.Find(root) != root)
                    Context.Report(violations, $"{prefix}group root {root} is not canonical in the context");

                if (overlay.TryGetClass(root, out var rootSlot) == false)
                    Context.Report(violations, $"{prefix}group root {root} is in no class of the overlay");

                var members = map.GroupMembers(g);
                if (members.IsEmpty)
                    Context.Report(violations, $"{prefix}group {root} has no member");

                for (var i = 0; i < members.Length; i++)
                {
                    var m = members[i];
                    if (i > 0 && members[i - 1] >= m)
                        Context.Report(violations, $"{prefix}the members of group {root} are not ascending at {m}");

                    if (blocks.Contains(m))
                        Context.Report(violations, $"{prefix}member {m} of group {root} is local to the context");

                    if (rootSlot >= 0 && (overlay.TryGetClass(m, out var memberSlot) == false || memberSlot != rootSlot))
                        Context.Report(violations, $"{prefix}member {m} of group {root} is not in the root's overlay class");

                    if (parentFinder.Find(m) != m)
                        Context.Report(violations, $"{prefix}member {m} of group {root} is not canonical in the parent");

                    if (finder.Find(m) != root)
                        Context.Report(violations, $"{prefix}member {m} of group {root} finds {finder.Find(m)} in the context");
                }
            }
        }

        /// <summary>
        /// Gets whether two member maps have the same groups and members.
        /// </summary>
        static bool SameMap(MemberMap a, MemberMap b)
        {
            if (a.GroupCount != b.GroupCount || a.GroupRoots.SequenceEqual(b.GroupRoots) == false)
                return false;

            for (var g = 0; g < a.GroupCount; g++)
                if (a.GroupMembers(g).SequenceEqual(b.GroupMembers(g)) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Records a split opened in this context as a scope. Thread-safe.
        /// </summary>
        /// <param name="record">The record.</param>
        internal void AddSplitRecord(SplitRecord record)
        {
            var records = LazyInitializer.EnsureInitialized(ref splitRecords);
            lock (records)
                records.Add(record);
        }

        /// <summary>
        /// Gets whether a live split record of this context splits <paramref name="source"/> over
        /// <paramref name="values"/>: the same source object, whichever driver opened the record and whatever the
        /// source's index in that driver's list, and the same cells. Thread-safe; O(records x cells).
        /// </summary>
        /// <param name="source">The source asking for the split.</param>
        /// <param name="values">The split's identifying cells.</param>
        /// <returns><c>true</c> if a live record covers the split.</returns>
        internal bool CoversSplit(ISplitSource source, ReadOnlySpan<uint> values)
        {
            ArgumentNullException.ThrowIfNull(source);
            var records = Volatile.Read(ref splitRecords);
            if (records is null)
                return false;

            lock (records)
            {
                foreach (var record in records)
                    if (record.IsLive && record.Splits(source, values))
                        return true;
            }

            return false;
        }

        /// <summary>
        /// Appends this context's live split records to <paramref name="into"/>, in creation order; only those
        /// <paramref name="driver"/> opened when it is not <c>null</c> (a split step runs and lifts every live record
        /// its own driver opened in the scope). Thread-safe; O(records).
        /// </summary>
        /// <param name="into">The list to append to.</param>
        /// <param name="driver">The driver whose records to copy; <c>null</c> copies every live record.</param>
        internal void CopyLiveSplitRecords(List<SplitRecord> into, SplitDriver? driver = null)
        {
            ArgumentNullException.ThrowIfNull(into);
            var records = Volatile.Read(ref splitRecords);
            if (records is null)
                return;

            lock (records)
            {
                foreach (var record in records)
                    if (record.IsLive && (driver is null || ReferenceEquals(record.Driver, driver)))
                        into.Add(record);
            }
        }

        /// <summary>
        /// Gets the number of split records this context holds, live or not.
        /// </summary>
        internal int SplitRecordCount
        {
            get
            {
                var records = Volatile.Read(ref splitRecords);
                if (records is null)
                    return 0;

                lock (records)
                    return records.Count;
            }
        }

        /// <summary>
        /// Removes and returns every split record of this context, in creation order, for
        /// <see cref="SplitDriver.DisposeRecords"/>. Thread-safe.
        /// </summary>
        /// <returns>The records; empty when there are none.</returns>
        internal SplitRecord[] TakeSplitRecords()
        {
            var records = Volatile.Read(ref splitRecords);
            if (records is null)
                return Array.Empty<SplitRecord>();

            lock (records)
            {
                var taken = records.ToArray();
                records.Clear();
                return taken;
            }
        }

    }

}
