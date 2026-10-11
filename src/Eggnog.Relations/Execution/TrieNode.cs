using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    /// Caches, both built on first use and kept for the node's life:
    /// <list type="bullet">
    /// <item>per column, a <see cref="ColumnIndex"/> grouping the node's rows by that column
    /// (<see cref="GetCachedIndex"/>, egglog's <c>get_cached_index</c>). When the caches are reset the index is
    /// disposed but its object is kept as a spare for that column, and the next build refills it in place
    /// (<see cref="ColumnIndex.RebuildForSubset"/>), so a recycled node builds its indexes without allocating;</item>
    /// <item>per column, a map from a value to the child node holding the node's rows with that value, refined by the
    /// atom's constraints (<see cref="TryGetChild"/> and <see cref="AddChild"/>, egglog's
    /// <c>get_cached_trie_node</c>). So a (node, column, value) is refined once per rule execution.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Ownership replaces egglog's <c>Arc</c> counting. A node is owned by exactly one of: its rule (a root node), the
    /// frame that created it (a sparse refinement, released after the frame's recursion), its parent's child cache, or
    /// a join position (a scratch node, reused for every dense refinement at that position, as egglog's
    /// <c>insert_subset</c> reuses a uniquely owned node). <see cref="Release"/> disposes the subset and the caches,
    /// releases the children and returns the node to a <see cref="TrieNodePool"/>. Nodes that parallel tasks may share
    /// are never released; the garbage collector reclaims them.
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

        Subset subset;
        ColumnIndex?[]? indexes;
        ColumnIndex?[]? spareIndexes;
        Dictionary<uint, TrieNode>?[]? children;
        bool concurrent;
        bool hasCaches;
        bool pooled;

        /// <summary>
        /// Gets the number of rows.
        /// </summary>
        public int Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => subset.Size;
        }

        /// <summary>
        /// Gets whether the node holds no row.
        /// </summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => subset.IsEmpty;
        }

        /// <summary>
        /// Gets the rows, valid while the node is not released or reset.
        /// </summary>
        public SubsetRef Rows
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => subset.AsRef();
        }

        /// <summary>
        /// Gets whether the node may be shared by parallel tasks, so its caches are published thread-safely.
        /// </summary>
        public bool IsConcurrent => concurrent;

        /// <summary>
        /// Gives a new or pooled node its rows. Called by <see cref="TrieNodePool.Rent"/>.
        /// </summary>
        /// <param name="rows">The rows; owned by the node from now on.</param>
        /// <param name="isConcurrent">Whether the node may be shared by parallel tasks.</param>
        internal void Initialize(Subset rows, bool isConcurrent)
        {
            Debug.Assert(hasCaches == false, "a pooled node kept its caches");
            subset = rows;
            concurrent = isConcurrent;
            pooled = false;
        }

        /// <summary>
        /// Narrows the rows to those also in <paramref name="other"/>: how a rule's headers restrict a root node before
        /// the join starts and before anything is cached on it (core-relations/src/free_join/execute.rs:526-539).
        /// </summary>
        /// <param name="other">The header subset.</param>
        public void Intersect(SubsetRef other)
        {
            Debug.Assert(hasCaches == false, "intersecting a node that has caches");
            subset.Intersect(other);
        }

        /// <summary>
        /// Gets the index of the node's rows by <paramref name="column"/>, building it on first use. egglog's
        /// <c>get_cached_index</c>: <see cref="ColumnIndex.BuildForSubset"/> over the live rows, O(rows) once, refilling
        /// the column's spare index when the node has one, so that a recycled node allocates nothing.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="table">The atom's table.</param>
        /// <returns>The index, owned by the node.</returns>
        public ColumnIndex GetCachedIndex(ColumnId column, Table table)
        {
            var col = column.Index;
            var array = concurrent ? Volatile.Read(ref indexes) : indexes;
            if (array is null || array.Length <= col)
                array = CreateIndexArray(array, Math.Max(table.Arity, col + 1));

            var index = concurrent ? Volatile.Read(ref array[col]) : array[col];
            if (index is not null)
                return index;

            var built = BuildIndex(col, column, table);
            hasCaches = true;
            if (concurrent == false)
            {
                array[col] = built;
                return built;
            }

            var prior = Interlocked.CompareExchange(ref array[col], built, null);
            if (prior is null)
                return built;

            built.Dispose();
            return prior;
        }

        /// <summary>
        /// Looks up the cached child for <paramref name="value"/> in <paramref name="column"/>. The read half of
        /// egglog's <c>get_cached_trie_node</c>.
        /// </summary>
        /// <param name="column">The column's index.</param>
        /// <param name="value">The value.</param>
        /// <param name="child">The child, when cached.</param>
        /// <returns><c>true</c> if the child is cached.</returns>
        public bool TryGetChild(int column, uint value, [NotNullWhen(true)] out TrieNode? child)
        {
            var maps = concurrent ? Volatile.Read(ref children) : children;
            if (maps is null || maps.Length <= column)
            {
                child = null;
                return false;
            }

            var map = concurrent ? Volatile.Read(ref maps[column]) : maps[column];
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
        /// Caches <paramref name="child"/> for <paramref name="value"/> in <paramref name="column"/> and returns the
        /// cached child: <paramref name="child"/>, or, if another thread cached one first, that one (and
        /// <paramref name="child"/> is released). The write half of egglog's <c>get_cached_trie_node</c>.
        /// </summary>
        /// <param name="column">The column's index.</param>
        /// <param name="value">The value.</param>
        /// <param name="child">The refined child; owned by this node from now on.</param>
        /// <param name="arity">The atom's arity, which sizes the per-column maps.</param>
        /// <param name="pool">Receives <paramref name="child"/> if it loses a race.</param>
        /// <returns>The cached child.</returns>
        public TrieNode AddChild(int column, uint value, TrieNode child, int arity, TrieNodePool pool)
        {
            var maps = concurrent ? Volatile.Read(ref children) : children;
            if (maps is null || maps.Length <= column)
                maps = CreateChildArray(maps, Math.Max(arity, column + 1));

            var map = (concurrent ? Volatile.Read(ref maps[column]) : maps[column]) ?? CreateChildMap(maps, column);
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
        /// Resets the caches and replaces the rows with the dense range <paramref name="range"/>: egglog's
        /// <c>insert_subset</c> on a uniquely owned node, used for a position's scratch node.
        /// </summary>
        /// <param name="range">The new rows.</param>
        /// <param name="pool">Receives the released children.</param>
        public void ResetDense(OffsetRange range, TrieNodePool pool)
        {
            ResetCaches(pool);
            subset.Dispose();
            subset = Subset.Dense(range);
        }

        /// <summary>
        /// Disposes the rows and the caches, releases the children, and returns the node to
        /// <paramref name="pool"/>. Only for a node its owner is done with and no other thread can reach.
        /// </summary>
        /// <param name="pool">The pool.</param>
        public void Release(TrieNodePool pool)
        {
            Debug.Assert(pooled == false, "a trie node was released twice");
            ResetCaches(pool);
            subset.Dispose();
            pooled = true;
            pool.Return(this);
        }

        /// <inheritdoc />
        public override string ToString() => subset.ToString();

        /// <summary>
        /// Builds the index of the node's rows by <paramref name="column"/>: refills the column's spare index, taken
        /// atomically when the node is concurrent, or builds a new one when there is none.
        /// </summary>
        /// <param name="col">The column's index.</param>
        /// <param name="column">The column.</param>
        /// <param name="table">The atom's table.</param>
        /// <returns>The index, not yet published.</returns>
        ColumnIndex BuildIndex(int col, ColumnId column, Table table)
        {
            var spares = concurrent ? Volatile.Read(ref spareIndexes) : spareIndexes;
            ColumnIndex? spare = null;
            if (spares is not null && spares.Length > col)
            {
                if (concurrent)
                {
                    spare = Interlocked.Exchange(ref spares[col], null);
                }
                else
                {
                    spare = spares[col];
                    spares[col] = null;
                }
            }

            if (spare is null)
                return table.GroupByColumn(subset.AsRef(), column);

            spare.RebuildForSubset(table, subset.AsRef(), column);
            return spare;
        }

        /// <summary>
        /// Keeps the disposed index <paramref name="index"/> as the spare of column <paramref name="col"/>, unless the
        /// column already has one (then <paramref name="index"/> is left to the garbage collector; its storage is
        /// already back in the pools). Only while no other thread can reach the node (<see cref="ResetCaches"/>), so the
        /// spare array is replaced without synchronization: a thread taking a spare from an array this copied could
        /// otherwise share the index with one taking it from the copy.
        /// </summary>
        /// <param name="col">The column's index.</param>
        /// <param name="index">The disposed index.</param>
        void KeepSpare(int col, ColumnIndex index)
        {
            var spares = spareIndexes;
            if (spares is null || spares.Length <= col)
            {
                var created = new ColumnIndex?[Math.Max(indexes?.Length ?? 0, col + 1)];
                spares?.CopyTo(created, 0);
                spareIndexes = spares = created;
            }

            spares[col] ??= index;
        }

        ColumnIndex?[] CreateIndexArray(ColumnIndex?[]? old, int size)
        {
            var created = new ColumnIndex?[size];
            if (concurrent == false)
            {
                indexes = created;
                return created;
            }

            var prior = Interlocked.CompareExchange(ref indexes, created, old);
            return ReferenceEquals(prior, old) ? created : prior!;
        }

        Dictionary<uint, TrieNode>?[] CreateChildArray(Dictionary<uint, TrieNode>?[]? old, int size)
        {
            var created = new Dictionary<uint, TrieNode>?[size];
            if (concurrent == false)
            {
                children = created;
                return created;
            }

            var prior = Interlocked.CompareExchange(ref children, created, old);
            return ReferenceEquals(prior, old) ? created : prior!;
        }

        Dictionary<uint, TrieNode> CreateChildMap(Dictionary<uint, TrieNode>?[] maps, int column)
        {
            var created = new Dictionary<uint, TrieNode>();
            if (concurrent == false)
            {
                maps[column] = created;
                return created;
            }

            return Interlocked.CompareExchange(ref maps[column], created, null) ?? created;
        }

    }

}
