using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The rows of one atom that the join is currently considering, with indexes and refined children built on them
    /// lazily: egglog's <c>TrieNode</c> (core-relations/src/free_join/execute.rs:662-750), the node of the lazy trie of
    /// Generic Join and Free Join.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows. A layered row set (<see cref="LayeredSubset"/>): at a root, and wherever the rows lie in one layer, a
    /// plain <see cref="Subset"/> and its layer, behind one flag test; in a child, rows of several layers of the atom's
    /// view (<see cref="IsMultiLayer"/>), held in the node's <see cref="TrieNodeSideState"/>. Rows of different layers
    /// are different rows. Keeping the multi-layer rows and the spare indexes out of line keeps a node at 64 bytes, so
    /// a parallel root run, which allocates one per frame at positions 0 and 1, pays nothing for layers.
    /// </para>
    /// <para>
    /// Caches, both built on first use and kept for the node's life:
    /// <list type="bullet">
    /// <item>per (layer, column), a <see cref="ColumnIndex"/> grouping the node's rows of that layer by that column
    /// (<see cref="GetCachedIndex(ColumnId, int, LayerView)"/>, egglog's <c>get_cached_index</c>), built over the rows the
    /// layer's view sees. Its slot is the column for a single-layer node and <c>layer × arity + column</c>
    /// for a multi-layer one. When the caches are reset the index is disposed but its object is kept as a spare for that
    /// slot, and the next build refills it in place
    /// (<see cref="ColumnIndex.RebuildForSubset(Table, SubsetRef, ColumnId, HiddenRows)"/>), so a recycled node builds its
    /// indexes without allocating;</item>
    /// <item>per child slot, a map from a value to the child node holding the node's rows with that value, refined by the
    /// atom's constraints (<see cref="TryGetChild"/> and <see cref="AddChild"/>, egglog's <c>get_cached_trie_node</c>).
    /// So a (node, slot, value) is refined once per rule execution. The slot (<see cref="ChildSlot"/>) is
    /// <c>selector × arity + column</c>: selector 0 is a refinement over every layer (a probed side, and every refinement
    /// of a single-layer node), selector <c>l + 1</c> one of layer <c>l</c> only (a driver's piece in layer <c>l</c> of a
    /// multi-layer node), so a slot always names one well-defined row set.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Ownership replaces egglog's <c>Arc</c> counting. A node is owned by exactly one of: its rule (a root node), the
    /// frame that created it (a sparse refinement, released after the frame's recursion), its parent's child cache, or
    /// a join position (a scratch node, reused for every dense refinement at that position, as egglog's
    /// <c>insert_subset</c> reuses a uniquely owned node). <see cref="Release"/> disposes the rows and the caches,
    /// releases the children and returns the node to a <see cref="TrieNodePool"/>, whose <see cref="TrieNodePool.Layers"/>
    /// takes back a multi-layer node's per-layer array. Nodes that parallel tasks may share are never released; the
    /// garbage collector reclaims them.
    /// </para>
    /// <para>
    /// Threading: a node created for a parallel run (<see cref="IsConcurrent"/>) publishes its caches with interlocked
    /// operations and locks a child map around each access, egglog's <c>OnceLock</c> and <c>RwLock</c>; a node of a
    /// serial run takes no locks. Two threads may build the same index or child at once; one result wins and the other
    /// is disposed, which is equivalent because the computation is deterministic.
    /// </para>
    /// </remarks>
    internal sealed class TrieNode
    {

        /// <summary>
        /// Child maps larger than this are dropped rather than cleared when the node's caches are reset, so pooled
        /// nodes do not pin large dictionaries.
        /// </summary>
        const int RetainedChildMapSize = 64;

        /// <summary>
        /// The rows of a single-layer node; empty while the node is multi-layer.
        /// </summary>
        Subset single;
        ColumnIndex?[]? indexes;
        Dictionary<uint, TrieNode>?[]? children;
        TrieNodeSideState? side;

        /// <summary>
        /// The layer of <see cref="single"/>.
        /// </summary>
        byte layer;

        /// <summary>
        /// Whether the rows are <see cref="side"/>'s multi-layer rows.
        /// </summary>
        bool multiLayer;
        bool concurrent;
        bool hasCaches;
        bool pooled;

        /// <summary>
        /// Gets the number of rows, over every layer.
        /// </summary>
        public int Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => multiLayer ? side!.Rows.Size : single.Size;
        }

        /// <summary>
        /// Gets whether the node holds no row.
        /// </summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => multiLayer == false && single.IsEmpty;
        }

        /// <summary>
        /// Gets the rows of a single-layer node (every root node), valid while the node is not released or reset.
        /// </summary>
        /// <exception cref="InvalidOperationException">The node is multi-layer; read it per layer with
        /// <see cref="GetLayerRows"/>.</exception>
        public SubsetRef Rows
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (multiLayer)
                    ThrowMultiLayer();

                return single.AsRef();
            }
        }

        /// <summary>
        /// Gets the layers that hold rows: bit <c>l</c> for layer <c>l</c>.
        /// </summary>
        public uint LayerMask
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => multiLayer ? side!.Rows.LayerMask : (single.IsEmpty ? 0u : 1u << layer);
        }

        /// <summary>
        /// Gets whether the rows lie in two or more layers. One flag test.
        /// </summary>
        public bool IsMultiLayer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => multiLayer;
        }

        /// <summary>
        /// Gets the layer of a single-layer node (0 for an empty one), or −1 for a multi-layer node.
        /// </summary>
        public int SingleLayer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => multiLayer ? -1 : layer;
        }

        /// <summary>
        /// Gets whether the node may be shared by parallel tasks, so its caches are published thread-safely.
        /// </summary>
        public bool IsConcurrent => concurrent;

        /// <summary>
        /// Gets a child-cache slot: <paramref name="selector"/> × <paramref name="arity"/> + <paramref name="column"/>,
        /// where selector 0 means a refinement over every layer and <c>l + 1</c> one of layer <c>l</c> only.
        /// </summary>
        /// <param name="column">The column refined on.</param>
        /// <param name="selector">0, or one more than the layer.</param>
        /// <param name="arity">The atom's arity.</param>
        /// <returns>The slot.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ChildSlot(int column, int selector, int arity) => selector * arity + column;

        /// <summary>
        /// Borrows the rows of layer <paramref name="layer"/>: empty when the layer holds none. Valid while the node is
        /// not released or reset.
        /// </summary>
        /// <param name="layer">The layer.</param>
        /// <returns>The rows.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubsetRef GetLayerRows(int layer)
        {
            if (multiLayer)
                return side!.Rows.GetLayer(layer);

            if ((uint)layer >= LayeredSubset.MaxLayers)
                ThrowLayerOutOfRange(layer);

            return layer == this.layer ? single.AsRef() : default;
        }

        /// <summary>
        /// Gives a new or pooled node its rows. Called by <see cref="TrieNodePool.Rent(LayeredSubset)"/>.
        /// </summary>
        /// <param name="rows">The rows; owned by the node from now on.</param>
        /// <param name="isConcurrent">Whether the node may be shared by parallel tasks.</param>
        internal void Initialize(LayeredSubset rows, bool isConcurrent)
        {
            Debug.Assert(hasCaches == false, "a pooled node kept its caches");
            SetRows(rows);
            concurrent = isConcurrent;
            pooled = false;
        }

        /// <summary>
        /// Gives a new or pooled node the rows <paramref name="rows"/> of layer <paramref name="layer"/>: the
        /// <see cref="Initialize(LayeredSubset, bool)"/> of a single-layer row set, without building one. Called by
        /// <see cref="TrieNodePool.Rent(int, Subset)"/>.
        /// </summary>
        /// <param name="layer">The layer of the rows.</param>
        /// <param name="rows">The rows; owned by the node from now on.</param>
        /// <param name="isConcurrent">Whether the node may be shared by parallel tasks.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Initialize(int layer, Subset rows, bool isConcurrent)
        {
            Debug.Assert(hasCaches == false, "a pooled node kept its caches");
            if ((uint)layer >= LayeredSubset.MaxLayers)
                ThrowLayerOutOfRange(layer);

            single = rows;
            this.layer = (byte)layer;
            multiLayer = false;
            concurrent = isConcurrent;
            pooled = false;
        }

        /// <summary>
        /// Narrows the rows to those also in <paramref name="other"/>, layer by layer: how a rule's headers restrict a root
        /// node before the join starts and before anything is cached on it (core-relations/src/free_join/execute.rs:526-539).
        /// Two single-layer subsets of one layer intersect as plain subsets, the root path.
        /// </summary>
        /// <param name="other">The header subset.</param>
        /// <param name="pool">The pool a per-layer array returns to when the rows collapse, or <c>null</c>.</param>
        public void Intersect(in LayeredSubset other, SubsetArrayPool? pool)
        {
            Debug.Assert(hasCaches == false, "intersecting a node that has caches");
            if (multiLayer == false && other.IsMultiLayer == false)
            {
                // The root path: two plain subsets, intersected in place when they lie in the same layer.
                if (other.SingleLayer == layer)
                    single.Intersect(other.SingleRows);
                else
                    single.Dispose();

                return;
            }

            var rows = TakeRows();
            rows.Intersect(in other, pool);
            SetRows(rows);
        }

        /// <summary>
        /// Narrows the rows of a single-layer node to those also in <paramref name="other"/>, of the same layer.
        /// </summary>
        /// <param name="other">The rows to keep.</param>
        public void Intersect(SubsetRef other)
        {
            Debug.Assert(hasCaches == false, "intersecting a node that has caches");
            Debug.Assert(multiLayer == false, "a multi-layer node is narrowed by a layered subset");
            single.Intersect(other);
        }

        /// <summary>
        /// Gets the index of the node's rows of layer <paramref name="layer"/> by <paramref name="column"/>, building it on
        /// first use: egglog's <c>get_cached_index</c>,
        /// <see cref="ColumnIndex.BuildForSubset(Table, SubsetRef, ColumnId, HiddenRows)"/> over the rows the layer's view
        /// sees (live and not hidden), O(rows) once, refilling the slot's spare index when the node has one, so that a
        /// recycled node allocates nothing.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="layer">The layer; the node's only layer when it is single-layer.</param>
        /// <param name="view">The atom's view of that layer.</param>
        /// <returns>The index, owned by the node.</returns>
        public ColumnIndex GetCachedIndex(ColumnId column, int layer, LayerView view) => GetCachedIndex(column, layer, view, out _);

        /// <summary>
        /// <see cref="GetCachedIndex(ColumnId, int, LayerView)"/>, reporting whether this call built the index (for the
        /// executor's counters, <see cref="ExecutorCounters"/>).
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="layer">The layer; the node's only layer when it is single-layer.</param>
        /// <param name="view">The atom's view of that layer.</param>
        /// <param name="built">Whether the index was built now rather than found cached.</param>
        /// <returns>The index, owned by the node.</returns>
        public ColumnIndex GetCachedIndex(ColumnId column, int layer, LayerView view, out bool built)
        {
            ArgumentNullException.ThrowIfNull(view);
            var arity = view.Table.Arity;
            var slot = multiLayer ? layer * arity + column.Index : column.Index;
            return GetCachedIndex(slot, column, layer, view.Table, view.Hidden, arity, out built);
        }

        /// <summary>
        /// Gets the index of a single-layer node's rows by <paramref name="column"/>, hiding nothing: the root form of
        /// <see cref="GetCachedIndex(ColumnId, int, LayerView)"/>.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="table">The atom's table.</param>
        /// <returns>The index, owned by the node.</returns>
        public ColumnIndex GetCachedIndex(ColumnId column, Table table)
        {
            ArgumentNullException.ThrowIfNull(table);
            Debug.Assert(multiLayer == false, "a multi-layer node is indexed per layer");
            return GetCachedIndex(column.Index, column, layer, table, HiddenRows.None, table.Arity, out _);
        }

        /// <summary>
        /// Looks up the cached child for <paramref name="value"/> in child slot <paramref name="slot"/>
        /// (<see cref="ChildSlot"/>; the column itself for a refinement over every layer). The read half of egglog's
        /// <c>get_cached_trie_node</c>.
        /// </summary>
        /// <param name="slot">The child slot.</param>
        /// <param name="value">The value.</param>
        /// <param name="child">The child, when cached.</param>
        /// <returns><c>true</c> if the child is cached.</returns>
        public bool TryGetChild(int slot, uint value, [NotNullWhen(true)] out TrieNode? child)
        {
            var maps = concurrent ? Volatile.Read(ref children) : children;
            if (maps is null || maps.Length <= slot)
            {
                child = null;
                return false;
            }

            var map = concurrent ? Volatile.Read(ref maps[slot]) : maps[slot];
            if (map is null)
            {
                child = null;
                return false;
            }

            if (concurrent == false)
                return map.TryGetValue(value, out child);

            lock (map)
                return map.TryGetValue(value, out child);
        }

        /// <summary>
        /// Caches <paramref name="child"/> for <paramref name="value"/> in child slot <paramref name="slot"/> and returns
        /// the cached child: <paramref name="child"/>, or, if another thread cached one first, that one (and
        /// <paramref name="child"/> is released). The write half of egglog's <c>get_cached_trie_node</c>.
        /// </summary>
        /// <param name="slot">The child slot.</param>
        /// <param name="value">The value.</param>
        /// <param name="child">The refined child; owned by this node from now on.</param>
        /// <param name="arity">The atom's arity, which sizes the per-slot maps.</param>
        /// <param name="pool">Receives <paramref name="child"/> if it loses a race.</param>
        /// <returns>The cached child.</returns>
        public TrieNode AddChild(int slot, uint value, TrieNode child, int arity, TrieNodePool pool)
        {
            var maps = concurrent ? Volatile.Read(ref children) : children;
            if (maps is null || maps.Length <= slot)
                maps = CreateChildArray(maps, Math.Max(SlotCount(arity, selectors: true), slot + 1));

            var map = (concurrent ? Volatile.Read(ref maps[slot]) : maps[slot]) ?? CreateChildMap(maps, slot);
            hasCaches = true;
            if (concurrent == false)
            {
                map.Add(value, child);
                return child;
            }

            lock (map)
            {
                if (map.TryGetValue(value, out var existing))
                {
                    child.Release(pool);
                    return existing;
                }

                map.Add(value, child);
                return child;
            }
        }

        /// <summary>
        /// Disposes the cached indexes and releases the cached children, keeping the rows: what egglog's
        /// <c>insert_subset</c> does to a reused node (<c>cached_subsets.take()</c>, <c>cached_children.take()</c>).
        /// Only for a node no other thread can reach.
        /// </summary>
        /// <param name="pool">Receives the released children.</param>
        public void ResetCaches(TrieNodePool pool)
        {
            if (hasCaches == false)
                return;

            if (indexes is { } array)
            {
                for (var i = 0; i < array.Length; i++)
                {
                    if (array[i] is not { } index)
                        continue;

                    index.Dispose();
                    array[i] = null;
                    KeepSpare(i, index);
                }
            }

            if (children is { } maps)
            {
                for (var i = 0; i < maps.Length; i++)
                {
                    var map = maps[i];
                    if (map is null || map.Count == 0)
                        continue;

                    foreach (var child in map.Values)
                        child.Release(pool);

                    if (map.Count > RetainedChildMapSize)
                        maps[i] = null;
                    else
                        map.Clear();
                }
            }

            hasCaches = false;
        }

        /// <summary>
        /// Resets the caches and replaces the rows with the dense range <paramref name="range"/> of layer
        /// <paramref name="layer"/>: egglog's <c>insert_subset</c> on a uniquely owned node, used for a position's scratch
        /// node.
        /// </summary>
        /// <param name="layer">The layer of the rows.</param>
        /// <param name="range">The new rows.</param>
        /// <param name="pool">Receives the released children, and a multi-layer node's per-layer array.</param>
        public void ResetDense(int layer, OffsetRange range, TrieNodePool pool)
        {
            if ((uint)layer >= LayeredSubset.MaxLayers)
                ThrowLayerOutOfRange(layer);

            ResetCaches(pool);
            DisposeRows(pool.Layers);
            single = Subset.Dense(range);
            this.layer = (byte)layer;
        }

        /// <summary>
        /// <see cref="ResetDense(int, OffsetRange, TrieNodePool)"/> in layer 0.
        /// </summary>
        /// <param name="range">The new rows.</param>
        /// <param name="pool">Receives the released children.</param>
        public void ResetDense(OffsetRange range, TrieNodePool pool) => ResetDense(0, range, pool);

        /// <summary>
        /// Disposes the rows and the caches, releases the children, and returns the node to
        /// <paramref name="pool"/>. Only for a node its owner is done with and no other thread can reach.
        /// </summary>
        /// <param name="pool">The pool.</param>
        public void Release(TrieNodePool pool)
        {
            Debug.Assert(pooled == false, "a trie node was released twice");
            ResetCaches(pool);
            DisposeRows(pool.Layers);
            pooled = true;
            pool.Return(this);
        }

        /// <inheritdoc />
        public override string ToString() => multiLayer ? side!.Rows.ToString() : single.ToString();

        /// <summary>
        /// Gives the node <paramref name="rows"/>, in place of rows already moved out or disposed: a single-layer row set
        /// becomes the plain subset and its layer, a multi-layer one goes to the side state (created on first use and
        /// kept with the node).
        /// </summary>
        /// <param name="rows">The rows; owned by the node from now on.</param>
        void SetRows(LayeredSubset rows)
        {
            if (rows.IsMultiLayer)
            {
                (side ??= new TrieNodeSideState()).Rows = rows;
                single = default;
                layer = 0;
                multiLayer = true;
                return;
            }

            single = rows.TakeSingle(out var l);
            layer = (byte)l;
            multiLayer = false;
        }

        /// <summary>
        /// Moves the rows out as a <see cref="LayeredSubset"/>, leaving the node empty.
        /// </summary>
        /// <returns>The rows, now owned by the caller.</returns>
        LayeredSubset TakeRows()
        {
            if (multiLayer)
            {
                multiLayer = false;
                return side!.Rows.Take();
            }

            var rows = LayeredSubset.Single(layer, single.Take());
            layer = 0;
            return rows;
        }

        /// <summary>
        /// Disposes the rows, returning a multi-layer node's per-layer array to <paramref name="layers"/>, and leaves the
        /// node empty in layer 0.
        /// </summary>
        /// <param name="layers">The pool of per-layer arrays, or <c>null</c>.</param>
        void DisposeRows(SubsetArrayPool? layers)
        {
            if (multiLayer)
            {
                side!.Rows.Dispose(layers);
                multiLayer = false;
            }
            else
            {
                single.Dispose();
            }

            layer = 0;
        }

        /// <summary>
        /// Gets the number of slots a cache array of this node needs for an atom of <paramref name="arity"/>: the arity
        /// for a single-layer node; for a multi-layer one, every layer up to its highest (indexes) or every selector
        /// (children). Fixed while the rows are, so the arrays do not grow while the node is in use.
        /// </summary>
        int SlotCount(int arity, bool selectors)
        {
            if (multiLayer == false)
                return arity;

            var top = 32 - BitOperations.LeadingZeroCount(side!.Rows.LayerMask);
            return (selectors ? top + 1 : top) * arity;
        }

        /// <summary>
        /// Gets the index in slot <paramref name="slot"/>, building it over the rows of layer <paramref name="layer"/> on
        /// first use.
        /// </summary>
        ColumnIndex GetCachedIndex(int slot, ColumnId column, int layer, Table table, HiddenRows hidden, int arity, out bool built)
        {
            var array = concurrent ? Volatile.Read(ref indexes) : indexes;
            if (array is null || array.Length <= slot)
                array = CreateIndexArray(array, Math.Max(SlotCount(arity, selectors: false), slot + 1));

            var index = concurrent ? Volatile.Read(ref array[slot]) : array[slot];
            if (index is not null)
            {
                built = false;
                return index;
            }

            var made = BuildIndex(slot, column, GetLayerRows(layer), table, hidden);
            hasCaches = true;
            built = true;
            if (concurrent == false)
            {
                array[slot] = made;
                return made;
            }

            var prior = Interlocked.CompareExchange(ref array[slot], made, null);
            if (prior is null)
                return made;

            made.Dispose();
            return prior;
        }

        /// <summary>
        /// Builds the index of <paramref name="layerRows"/> by <paramref name="column"/>: refills the slot's spare index,
        /// taken atomically when the node is concurrent, or builds a new one when there is none. With
        /// <see cref="HiddenRows.None"/> both are the plain builds.
        /// </summary>
        /// <param name="slot">The index slot.</param>
        /// <param name="column">The column.</param>
        /// <param name="layerRows">The node's rows of the layer.</param>
        /// <param name="table">The layer's table.</param>
        /// <param name="hidden">The rows of the layer the view hides.</param>
        /// <returns>The index, not yet published.</returns>
        ColumnIndex BuildIndex(int slot, ColumnId column, SubsetRef layerRows, Table table, HiddenRows hidden)
        {
            var state = concurrent ? Volatile.Read(ref side) : side;
            var spares = state?.Spares;
            ColumnIndex? spare = null;
            if (spares is not null && spares.Length > slot)
            {
                if (concurrent)
                {
                    spare = Interlocked.Exchange(ref spares[slot], null);
                }
                else
                {
                    spare = spares[slot];
                    spares[slot] = null;
                }
            }

            if (spare is null)
                return table.GroupByColumn(layerRows, column, hidden);

            spare.RebuildForSubset(table, layerRows, column, hidden);
            return spare;
        }

        /// <summary>
        /// Keeps the disposed index <paramref name="index"/> as the spare of slot <paramref name="slot"/>, unless the slot
        /// already has one (then <paramref name="index"/> is left to the garbage collector; its storage is already back in
        /// the pools). Only while no other thread can reach the node (<see cref="ResetCaches"/>), so the spare array is
        /// replaced without synchronization: a thread taking a spare from an array this copied could otherwise share the
        /// index with one taking it from the copy.
        /// </summary>
        /// <param name="slot">The index slot.</param>
        /// <param name="index">The disposed index.</param>
        void KeepSpare(int slot, ColumnIndex index)
        {
            var state = side ??= new TrieNodeSideState();
            var spares = state.Spares;
            if (spares is null || spares.Length <= slot)
            {
                var created = new ColumnIndex?[Math.Max(indexes?.Length ?? 0, slot + 1)];
                spares?.CopyTo(created, 0);
                state.Spares = spares = created;
            }

            spares[slot] ??= index;
        }

        /// <summary>
        /// Replaces the index array with one of <paramref name="size"/> slots holding the old one's entries. The slot
        /// count is fixed by the rows, so this happens at most once per node and row set; a concurrent loser of the race
        /// reads the winner's array.
        /// </summary>
        ColumnIndex?[] CreateIndexArray(ColumnIndex?[]? old, int size)
        {
            var created = new ColumnIndex?[size];
            old?.CopyTo(created, 0);
            if (concurrent == false)
            {
                indexes = created;
                return created;
            }

            var prior = Interlocked.CompareExchange(ref indexes, created, old);
            return ReferenceEquals(prior, old) ? created : prior!;
        }

        /// <summary>
        /// Replaces the child-map array with one of <paramref name="size"/> slots holding the old one's maps; see
        /// <see cref="CreateIndexArray"/>.
        /// </summary>
        Dictionary<uint, TrieNode>?[] CreateChildArray(Dictionary<uint, TrieNode>?[]? old, int size)
        {
            var created = new Dictionary<uint, TrieNode>?[size];
            old?.CopyTo(created, 0);
            if (concurrent == false)
            {
                children = created;
                return created;
            }

            var prior = Interlocked.CompareExchange(ref children, created, old);
            return ReferenceEquals(prior, old) ? created : prior!;
        }

        Dictionary<uint, TrieNode> CreateChildMap(Dictionary<uint, TrieNode>?[] maps, int slot)
        {
            var created = new Dictionary<uint, TrieNode>();
            if (concurrent == false)
            {
                maps[slot] = created;
                return created;
            }

            return Interlocked.CompareExchange(ref maps[slot], created, null) ?? created;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowLayerOutOfRange(int layer)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), layer, $"A layer lies in [0, {LayeredSubset.MaxLayers}).");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowMultiLayer()
        {
            throw new InvalidOperationException("The node's rows span several layers; read them per layer with GetLayerRows.");
        }

    }

}
