using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A child context's layering engine: its lazily created <see cref="TableLayer"/>s, the layer lock and the
    /// deferred-adoption list, the cached <see cref="TableView"/>s and their dirty flags, the hidden sets awaiting a
    /// seal, its cursors into its ancestors, its <see cref="RetractionGuard"/> and its <see cref="LayerStatistics"/>.
    /// Reachable as <see cref="Context.Tables"/> and <see cref="Database.Layers"/>; the root has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layers. A layer is created by <see cref="GetOrCreateLayer"/> on the context's first write, hide or clear of a
    /// table: double-checked under the layer lock, published to the database's catalog
    /// (<see cref="TableCatalog.Publish"/>) and to the layer slots with volatile writes, and adopted into the
    /// database's table map at once, or, during a search (<see cref="InSearch"/>), at the next merge round
    /// (<see cref="AdoptDeferred"/>). Creating an empty layer changes no view and no <see cref="ViewGeneration"/>: it
    /// adds no visible row and hides nothing.
    /// </para>
    /// <para>
    /// Threads. Everything runs on the context's domain thread, except that the tasks of the context's own rule run may
    /// create layers; they touch only the catalog and the layer slots, both copy-on-write under the layer lock. A
    /// frozen context never grows or writes its arrays: it answers for a table added after its freeze through its
    /// parent.
    /// </para>
    /// <para>
    /// Rebuild. <see cref="CatchUpRows"/> absorbs what the context's unsettled ancestors appended since its cursors,
    /// and <see cref="RebuildPass"/> rewrites the context's own rows over the ids displaced since its last pass, hides
    /// and copies the ancestor rows naming its own losers, and merges; both copy through <see cref="StageCopy"/> and
    /// hide only once the copies are staged. <see cref="ScanOwn{TSink}"/> and
    /// <see cref="ScanAncestorsMentioning{TSink}"/> are the lift's reads, and <see cref="CheckInvariants"/> is the
    /// tables part of the context's checker.
    /// </para>
    /// </remarks>
    internal sealed partial class ContextTables : ILayerViewSource
    {

        readonly LayeredSchema schema;
        readonly Context owner;
        readonly Context[] chain;
        readonly int depth;
        readonly object layerLock = new();
        readonly RetractionGuard guard;
        readonly LayerStatistics statistics = new();

        /// <summary>
        /// The hidden sets that gained a hide since the last seal, with the ancestor layer each hides rows of. Domain
        /// thread only.
        /// </summary>
        readonly List<(HiddenSet Set, SortedWritesTable Layer)> unsealed = new();

        /// <summary>
        /// The layers by table index. Grown copy-on-write and published whole under the layer lock; each slot is written
        /// once, with a volatile write, under the lock.
        /// </summary>
        TableLayer?[] slots;

        /// <summary>
        /// The layers created during a search and not yet adopted into the database's table map; under the layer lock.
        /// </summary>
        List<TableLayer>? deferred;

        /// <summary>
        /// Whether the context is frozen (or freezing): its views fold every hide into their frozen part, and its arrays
        /// never change again.
        /// </summary>
        volatile bool frozen;

        volatile bool inSearch;

        /// <summary>
        /// Initializes the layering of <paramref name="owner"/>, a child context, with no layer, and its cursors at the
        /// current state of each ancestor that is not a leading frozen one.
        /// </summary>
        /// <param name="schema">The store's schema.</param>
        /// <param name="owner">The child context; its ancestors and database are set.</param>
        public ContextTables(LayeredSchema schema, Context owner)
        {
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(owner);
            if (owner.IsRoot)
                throw new ArgumentException("The root context is not layered.", nameof(owner));

            this.schema = schema;
            this.owner = owner;
            chain = owner.Ancestors.ToArray();
            depth = owner.Depth;
            guard = new RetractionGuard(schema, owner);
            var count = schema.TableCount;
            slots = new TableLayer?[count];
            views = new TableView?[count];
            dirty = new bool[count];
            cursors = new AncestorCursor?[depth];
            creationRows = new TableVersion[]?[depth];
            InitializeCursors();
        }

        /// <summary>
        /// Gets the owning context's depth: the index of its own layer in its views.
        /// </summary>
        public int Depth => depth;

        /// <summary>
        /// Gets the owning context.
        /// </summary>
        public Context Owner => owner;

        /// <summary>
        /// Gets the store's schema.
        /// </summary>
        public LayeredSchema Schema => schema;

        /// <summary>
        /// Gets the context's retraction guard.
        /// </summary>
        public RetractionGuard Guard => guard;

        /// <summary>
        /// Gets the counts of what the layering did.
        /// </summary>
        public LayerStatistics Statistics => statistics;

        /// <summary>
        /// Gets or sets whether the context's rule run is searching: layers created meanwhile wait for the next merge
        /// round to enter the database's table map. Set by <see cref="Database.RunRuleSet"/> around the search.
        /// </summary>
        internal bool InSearch
        {
            get => inSearch;
            set => inSearch = value;
        }

        /// <summary>
        /// Gets the number of hidden sets awaiting a seal: zero outside a merge of the context.
        /// </summary>
        internal int UnsealedCount => unsealed.Count;

        /// <summary>
        /// Gets whether the context's layering is frozen.
        /// </summary>
        internal bool IsFrozen => frozen;

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/> in the context: <see cref="Context.UnionFind"/>.
        /// </summary>
        /// <param name="id">Any id.</param>
        /// <returns>The canonical id.</returns>
        public uint Find(uint id) => owner.UnionFind.Find(id);

        /// <summary>
        /// Gets the table writes to <paramref name="table"/> are staged into (the catalog's resolver): the context's
        /// union-find table for the union-find, otherwise the context's layer of the table, created on first use.
        /// Thread-safe.
        /// </summary>
        /// <param name="table">The table staged into.</param>
        /// <returns>The table.</returns>
        /// <exception cref="InvalidOperationException">The layer cannot be created: the context is frozen, disposed,
        /// faulted or contradicted, or the table's merge is not a lattice merge, which only the root may
        /// write.</exception>
        /// <exception cref="KeyNotFoundException">The table is not registered with the store.</exception>
        internal Table ResolveForStaging(TableId table)
        {
            return table == schema.UnionFind ? owner.UnionFindTable : GetOrCreateLayer(table).Own;
        }

        /// <summary>
        /// Gets the context's layer of <paramref name="table"/>, or <c>null</c> when it has none (or the id lies beyond
        /// a frozen context's arrays). Lock-free.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The layer, or <c>null</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TableLayer? TryGetLayer(TableId table)
        {
            var current = Volatile.Read(ref slots);
            return (uint)table.Index < (uint)current.Length ? Volatile.Read(ref current[table.Index]) : null;
        }

        /// <summary>
        /// Gets the context's layer of <paramref name="table"/>, creating it on first use. Thread-safe: the tasks of
        /// the context's rule run may call it.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The layer.</returns>
        /// <exception cref="InvalidOperationException">The layer cannot be created: the context is frozen, disposed,
        /// faulted or contradicted, or the table is shared and its merge is not a lattice merge, which only the root
        /// may write (copying a row on touch counts as a write).</exception>
        /// <exception cref="KeyNotFoundException">The table is not registered with the store.</exception>
        internal TableLayer GetOrCreateLayer(TableId table)
        {
            return TryGetLayer(table) ?? CreateLayer(table);
        }

        /// <summary>
        /// Moves the layers created during a search into the database's table map: the prologue of every merge round
        /// (<see cref="Database.MergeAll"/>), on the domain thread. One volatile read when there are none.
        /// </summary>
        internal void AdoptDeferred()
        {
            if (Volatile.Read(ref deferred) is null)
                return;

            List<TableLayer>? adopted;
            lock (layerLock)
            {
                adopted = deferred;
                deferred = null;
            }

            if (adopted is null)
                return;

            foreach (var layer in adopted)
                owner.Database.AdoptLayer(layer);
        }

        /// <summary>
        /// Discards every write staged in the layers created during the search that just failed, which still wait on
        /// the deferred list and so are not in the database's table map that <see cref="Database.ExecuteRuleSet"/>'s
        /// rollback walks: an exception in a child's search faults it, and the rollback then leaves no staged write
        /// behind. Called on the domain thread after every task of the search has finished; the layers stay deferred,
        /// empty. O(deferred layers).
        /// </summary>
        internal void DiscardDeferredStaged()
        {
            if (Volatile.Read(ref deferred) is null)
                return;

            lock (layerLock)
            {
                if (deferred is not { } pending)
                    return;

                foreach (var layer in pending)
                    layer.Own.Pending.DiscardSince(null);
            }
        }

        /// <summary>
        /// Registers a hidden set that just gained a hide while sealed, for the next <see cref="SealHidden"/>. Domain
        /// thread only.
        /// </summary>
        /// <param name="set">The set.</param>
        /// <param name="layer">The ancestor layer it hides rows of.</param>
        internal void NoteUnsealed(HiddenSet set, SortedWritesTable layer) => unsealed.Add((set, layer));

        /// <summary>
        /// Records a hide in <paramref name="table"/>'s layer: the context's change stamp moves, the table's views are
        /// dropped at the next invalidation, and the hide is counted.
        /// </summary>
        /// <param name="table">The table.</param>
        internal void NoteHidden(TableId table)
        {
            owner.Database.NoteLayerChange();
            MarkDirty(table);
            statistics.RowsHidden++;
        }

        /// <summary>
        /// Gets the table of the layer at depth <paramref name="layer"/> of <paramref name="table"/>'s views: the root's
        /// table at depth 0, an ancestor's layer above it, the context's own layer at its own depth; <c>null</c> where that
        /// context has no layer.
        /// </summary>
        /// <param name="layer">A depth, at most <see cref="Depth"/>.</param>
        /// <param name="table">The table.</param>
        /// <returns>The layer's table, or <c>null</c>.</returns>
        internal SortedWritesTable? AncestorLayer(int layer, TableId table)
        {
            if (layer == depth)
                return TryGetLayer(table)?.Own;

            if (layer == 0)
                return schema[table]?.Root;

            return chain[layer].Tables!.TryGetLayer(table)?.Own;
        }

        /// <summary>
        /// Gets the database entry of the layer at depth <paramref name="layer"/> of <paramref name="table"/>'s views; see
        /// <see cref="AncestorLayer"/>.
        /// </summary>
        /// <param name="layer">A depth, at most <see cref="Depth"/>.</param>
        /// <param name="table">The table.</param>
        /// <returns>The entry, or <c>null</c>.</returns>
        internal TableInfo? AncestorInfo(int layer, TableId table)
        {
            if (layer == depth)
                return TryGetLayer(table)?.Info;

            if (layer == 0)
                return schema[table]?.RootInfo;

            return chain[layer].Tables!.TryGetLayer(table)?.Info;
        }

        /// <summary>
        /// Gets the deepest level in <c>(0, Depth]</c> of the chain that cleared <paramref name="table"/>, or 0: the
        /// layers below it are absent from the context's views. O(depth).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The level.</returns>
        internal int ClearedFloor(TableId table)
        {
            for (var level = depth; level > 0; level--)
                if (LayerAt(level, table) is { IsCleared: true })
                    return level;

            return 0;
        }

        /// <summary>
        /// Gets whether some level in <c>(layer, Depth]</c> hides row <paramref name="row"/> of the layer at depth
        /// <paramref name="layer"/>, sealed or not: the write path's visibility test. O(depth − layer).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="layer">The depth of the row's layer.</param>
        /// <param name="row">The row id.</param>
        /// <returns><c>true</c> if the context does not see the row.</returns>
        internal bool HiddenBetween(TableId table, int layer, int row)
        {
            for (var level = layer + 1; level <= depth; level++)
                if (LayerAt(level, table) is { } l && l.IsHiddenHere(layer, row))
                    return true;

            return false;
        }

        /// <summary>
        /// Drops every structure of the layering: the context is disposed. Its descendants are disposed already, so
        /// nothing reads its layers or views any more.
        /// </summary>
        internal void Release()
        {
            lock (layerLock)
            {
                Volatile.Write(ref slots, Array.Empty<TableLayer?>());
                Volatile.Write(ref views, Array.Empty<TableView?>());
                dirty = Array.Empty<bool>();
                anyDirty = false;
                deferred = null;
            }

            unsealed.Clear();
        }

        /// <summary>
        /// Gets the layer of the context at depth <paramref name="level"/> of the chain (this one included), or <c>null</c>.
        /// </summary>
        TableLayer? LayerAt(int level, TableId table) => level == depth ? TryGetLayer(table) : chain[level].Tables!.TryGetLayer(table);

        /// <summary>
        /// The locked half of <see cref="GetOrCreateLayer"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        TableLayer CreateLayer(TableId table)
        {
            lock (layerLock)
            {
                if (TryGetLayer(table) is { } existing)
                    return existing;

                if (frozen || owner.IsFrozen)
                    throw new InvalidOperationException("A frozen context cannot be written.");

                owner.ThrowIfDisposed();
                owner.ThrowIfFaulted();
                owner.ThrowIfContradicted();
                var s = schema[table] ?? throw new KeyNotFoundException($"Table {table} is not registered with the store.");
                if (s.IsLattice == false && s.IsContextPrivate == false)
                    ThrowNotLattice(s);

                var layer = new TableLayer(this, s);
                owner.Database.Catalog.Publish(table, layer.Own);
                var current = slots;
                if (table.Index >= current.Length)
                    current = GrowLocked(table.Index);

                Volatile.Write(ref current[table.Index], layer);
                if (inSearch)
                    (deferred ??= new List<TableLayer>()).Add(layer);
                else
                    owner.Database.AdoptLayer(layer);

                MarkDirtyLocked(table);
                statistics.LayersCreated++;
                return layer;
            }
        }

        /// <summary>
        /// Grows the slot, view and dirty arrays to hold <paramref name="index"/> (and every table registered now), copying
        /// and publishing each whole. Under the layer lock; never once frozen.
        /// </summary>
        /// <returns>The new slot array.</returns>
        TableLayer?[] GrowLocked(int index)
        {
            var length = Math.Max(index + 1, schema.TableCount);
            var nextSlots = new TableLayer?[length];
            Array.Copy(slots, nextSlots, slots.Length);
            var nextViews = new TableView?[length];
            Array.Copy(views, nextViews, views.Length);
            var nextDirty = new bool[length];
            Array.Copy(dirty, nextDirty, dirty.Length);
            Volatile.Write(ref views, nextViews);
            dirty = nextDirty;
            Volatile.Write(ref slots, nextSlots);
            return nextSlots;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNotLattice(LayeredTableSchema schema)
        {
            throw new InvalidOperationException($"Table {schema.Name ?? schema.Id.ToString()} has a merge that is not a lattice merge, so only the root context may write it; a child's first write, hide or copy of one of its rows is refused.");
        }

    }

}
