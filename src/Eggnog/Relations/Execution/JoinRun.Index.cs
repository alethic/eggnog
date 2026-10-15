using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class JoinRun
    {

        /// <summary>
        /// Moves <paramref name="atom"/>'s node out of the bindings into <paramref name="prober"/> and gives it an
        /// index over <paramref name="columns"/> per layer of the node's rows (<see cref="BuildPiece"/>). egglog's
        /// <c>get_index</c> (core-relations/src/free_join/execute.rs:819-884) for each layer; a single-layer node
        /// (every root node) gets one piece, built exactly as egglog builds its index.
        /// </summary>
        /// <remarks>
        /// An atom with a plain view (<see cref="plainViews"/>, every root view) takes one null test more than egglog's
        /// <c>get_index</c> and builds its one piece inline; other atoms build their pieces in
        /// <see cref="GetIndexLayered"/>. Kept out of line itself, so that the kernels that call it once per stage stay
        /// small.
        /// </remarks>
        /// <param name="prober">Receives the node and the pieces; its spare piece array is kept.</param>
        /// <param name="atom">The atom's index.</param>
        /// <param name="columns">The columns to index, at least one.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void GetIndex(ref Prober prober, int atom, ReadOnlySpan<ColumnId> columns)
        {
            var node = nodes[atom]!;
            nodes[atom] = null;
            prober.Node = node;
            prober.RestCount = 0;
            var root = ExecutorCounters.Enabled && IsRoot(atom, node);
            if (plainViews[atom] is { } plain)
            {
                BuildPiece(ref prober.First, node, 0, plain, node.SingleRows, columns, root);
                return;
            }

            GetIndexLayered(ref prober, node, atomViews[atom]!, columns, root);
        }

        /// <summary>
        /// <see cref="GetIndex"/> for an atom whose view is not plain: one piece per layer of <paramref name="node"/>'s rows,
        /// the first in <see cref="Prober.First"/> and the others in the prober's spare piece array.
        /// </summary>
        /// <param name="prober">Receives the pieces; holds the node and no piece beyond the first.</param>
        /// <param name="node">The atom's node.</param>
        /// <param name="view">The atom's view.</param>
        /// <param name="columns">The columns to index, at least one.</param>
        /// <param name="root">Whether the node is the atom's root node, for the executor's counters.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void GetIndexLayered(ref Prober prober, TrieNode node, TableView view, ReadOnlySpan<ColumnId> columns, bool root)
        {
            if (node.IsMultiLayer == false)
            {
                var layer = node.SingleLayer;
                BuildPiece(ref prober.First, node, layer, view.Layers[layer]!, node.Rows, columns, root);
                return;
            }

            var remaining = node.LayerMask;
            var first = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            BuildPiece(ref prober.First, node, first, view.Layers[first]!, node.GetLayerRows(first), columns, root);
            while (remaining != 0)
            {
                var layer = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                BuildPiece(ref prober.AddPiece(), node, layer, view.Layers[layer]!, node.GetLayerRows(layer), columns, root);
            }
        }

        /// <summary>
        /// Builds the index of one layer of an atom's rows: egglog's <c>get_index</c> choice per layer.
        /// <list type="bullet">
        /// <item>at most eight rows and one column: a <see cref="SparseColumnIndex"/> over the rows the view sees;</item>
        /// <item>a dense range of more than half the layer, every column cacheable: the layer's cached index (its
        /// <see cref="LayerView.Info"/>), shared, and lock-free once its context is frozen; clamped to the range unless it
        /// is the whole layer;</item>
        /// <item>otherwise several columns: a <see cref="TupleIndex"/> built for this stage over the rows the view
        /// sees;</item>
        /// <item>otherwise one column: the <see cref="ColumnIndex"/> cached on the node for the layer, over the rows the
        /// view sees.</item>
        /// </list>
        /// Only a cached index can list rows the view does not see (tombstones, and rows it hides: hides are not index
        /// content), so only its pieces refine for visibility (<see cref="ProberPiece.MayHoldInvisible"/>).
        /// <paramref name="root"/> (whether the node is the atom's root node) only splits the executor's counters.
        /// </summary>
        /// <param name="piece">The piece; overwritten.</param>
        /// <param name="node">The atom's node.</param>
        /// <param name="layer">The layer.</param>
        /// <param name="lv">The atom's view of the layer.</param>
        /// <param name="rows">The node's rows of the layer.</param>
        /// <param name="columns">The columns to index, at least one.</param>
        /// <param name="root">Whether the node is the atom's root node, for the executor's counters.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void BuildPiece(ref ProberPiece piece, TrieNode node, int layer, LayerView lv, SubsetRef rows, ReadOnlySpan<ColumnId> columns, bool root)
        {
            piece = default;
            piece.Layer = (byte)layer;
            piece.View = lv;
            var table = lv.Table;
            if (rows.Size <= SparseColumnIndex.Capacity && columns.Length == 1)
            {
                var sparse = RentSparse();
                sparse.Build(table, rows, columns[0], lv.Hidden);
                piece.Kind = ProberKind.Sparse;
                piece.Sparse = sparse;
                if (ExecutorCounters.Enabled)
                    counters!.NoteDynamicIndex(layer < depth, root, rows.Size);

                return;
            }

            var info = lv.Info;
            var cacheable = true;
            foreach (var column in columns)
                cacheable &= info.Spec.IsUncacheable(column) == false;

            // Every layer's whole row set is the dense range [0, physical rows).
            var whole = table.All();
            var wholeSize = whole.Size;
            whole.Dispose();
            if (cacheable && rows.TryGetDense(out var range) && wholeSize / 2 < rows.Size)
            {
                // A large share of the layer: probe its cached index, clamped to the range unless it is the layer.
                piece.HasOuter = (range.Start == 0 && range.End == wholeSize) == false;
                piece.Outer = range;
                piece.MayHoldInvisible = lv.HasInvisible;
                if (columns.Length != 1)
                {
                    piece.Kind = ProberKind.CachedTuple;
                    piece.Tuple = info.GetIndex(columns).Data;
                }
                else
                {
                    piece.Kind = ProberKind.CachedColumn;
                    piece.Column = info.GetColumnIndex(columns[0]).Data;
                }

                if (ExecutorCounters.Enabled)
                    counters!.NoteCachedPiece(layer < depth);

                return;
            }

            if (columns.Length != 1)
            {
                // egglog has no caching strategy for multi-column dynamic indexes either: built per stage invocation.
                piece.Kind = ProberKind.DynamicTuple;
                piece.Tuple = table.GroupByKey(rows, columns, lv.Hidden);
                if (ExecutorCounters.Enabled)
                    counters!.NoteDynamicIndex(layer < depth, root, rows.Size);

                return;
            }

            piece.Kind = ProberKind.DynamicColumn;
            piece.Column = node.GetCachedIndex(columns[0], layer, lv, out var built);
            if (ExecutorCounters.Enabled && built)
                counters!.NoteDynamicIndex(layer < depth, root, rows.Size);
        }

        /// <summary>
        /// Moves <paramref name="prober"/>'s node back into the bindings and releases what its pieces own. egglog's
        /// <c>move_back</c>. The spare piece array is kept for the slot's next use.
        /// </summary>
        /// <param name="prober">The prober; cleared.</param>
        /// <param name="atom">The atom's index.</param>
        void MoveBack(ref Prober prober, int atom)
        {
            nodes[atom] = prober.Node;
            ReleasePiece(ref prober.First);
            if (prober.RestCount != 0)
                ReleaseRest(ref prober);

            prober.Node = null!;
        }

        /// <summary>
        /// Releases the pieces of <paramref name="prober"/> beyond the first, keeping its spare piece array: the
        /// multi-layer half of <see cref="MoveBack"/>.
        /// </summary>
        /// <param name="prober">The prober; left with no piece beyond the first.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReleaseRest(ref Prober prober)
        {
            var rest = prober.Rest!;
            for (var i = 0; i < prober.RestCount; i++)
                ReleasePiece(ref rest[i]);

            prober.RestCount = 0;
        }

        /// <summary>
        /// Releases what a piece owns: a sparse index goes back to the task's stack, a per-stage tuple index is disposed.
        /// </summary>
        void ReleasePiece(ref ProberPiece piece)
        {
            switch (piece.Kind)
            {
                case ProberKind.Sparse:
                    ReturnSparse(piece.Sparse!);
                    break;
                case ProberKind.DynamicTuple:
                    piece.Tuple!.Dispose();
                    break;
            }

            piece = default;
        }

        /// <summary>
        /// Copies a candidate subset of one layer and keeps the rows that the view sees and that satisfy
        /// <paramref name="constraints"/>. egglog's <c>refine_subset</c> (core-relations/src/free_join/execute.rs:1091-1107):
        /// visibility is refined only when the subset came from an index that may list invisible rows
        /// (<see cref="LayerView.RefineVisible"/>: tombstones, and the rows the view hides), and the table's constraint
        /// refinement itself drops tombstones.
        /// </summary>
        /// <param name="rows">The candidate rows of the layer.</param>
        /// <param name="constraints">The slow constraints of the scan or probe.</param>
        /// <param name="lv">The atom's view of the layer.</param>
        /// <param name="mayHoldInvisible">Whether <paramref name="rows"/> may list rows the view does not see.</param>
        /// <returns>The refined rows, owned by the caller.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Subset RefineVisible(SubsetRef rows, Constraint[] constraints, LayerView lv, bool mayHoldInvisible)
        {
            var subset = rows.ToOwned();
            if (mayHoldInvisible)
                subset = lv.RefineVisible(subset);

            if (constraints.Length != 0)
                subset = lv.Table.Refine(subset, constraints);

            return subset;
        }

        /// <summary>
        /// Refines the atom of <paramref name="scan"/> to its rows of <paramref name="piece"/>'s layer holding
        /// <paramref name="key"/>, appending the refinement to <paramref name="updates"/>: a small candidate is refined and
        /// copied into the frame; a large one goes through the node's child cache, so it is refined once per rule execution
        /// (core-relations/src/free_join/execute.rs:1124-1143). A multi-layer node caches it under the piece's layer
        /// selector: the rows of that layer only.
        /// </summary>
        /// <param name="updates">The frame buffer.</param>
        /// <param name="node">The atom's node, held by the scan's prober.</param>
        /// <param name="piece">The piece the rows come from.</param>
        /// <param name="scan">The scan.</param>
        /// <param name="key">The value bound.</param>
        /// <param name="rows">The atom's candidate rows of the layer holding the value.</param>
        /// <returns><c>false</c> if no row survives; the caller rolls the frame back.</returns>
        bool RefineScan(FrameUpdates updates, TrieNode node, in ProberPiece piece, SingleScanSpec scan, uint key, SubsetRef rows)
        {
            if (rows.Size <= SmallRefinement)
            {
                var subset = RefineVisible(rows, scan.Constraints, piece.View!, piece.MayHoldInvisible);
                if (subset.IsEmpty)
                {
                    subset.Dispose();
                    return false;
                }

                updates.RefineAtomSubset(scan.Atom, piece.Layer, subset, pool);
                return true;
            }

            var column = scan.Column.Index;
            var slot = node.IsMultiLayer ? TrieNode.ChildSlot(column, piece.Layer + 1, piece.View!.Table.Arity) : column;
            if (node.TryGetChild(slot, key, out var child) == false)
            {
                var lv = piece.View!;
                var subset = RefineVisible(rows, scan.Constraints, lv, piece.MayHoldInvisible);
                child = node.AddChild(slot, key, pool.Rent(piece.Layer, subset), lv.Table.Arity, pool);
            }

            if (child.IsEmpty)
                return false;

            updates.RefineAtom(scan.Atom, child);
            return true;
        }

        /// <summary>
        /// Probes the scan's prober with <paramref name="key"/> and refines its atom to the rows holding it, over every
        /// layer: the probed side of an intersection. One piece takes egglog's path (<see cref="RefineScan"/>); several
        /// join their hits across layers (<see cref="RefineScanHits"/>).
        /// </summary>
        /// <returns><c>false</c> on a miss or when no row survives; the caller rolls the frame back.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ProbeRefineScan(FrameUpdates updates, ref Prober prober, SingleScanSpec scan, uint key, int cur)
        {
            if (prober.PieceCount == 1)
                return prober.First.TryGetSubset(key, out var rows) && RefineScan(updates, prober.Node, prober.First, scan, key, rows);

            var layerHits = TakeHits(cur);
            return ProbeAll(ref prober, key, layerHits) && RefineScanHits(updates, ref prober, scan, key, layerHits);
        }

        /// <summary>
        /// Refines a probed atom of an intersection to the rows of several layers holding <paramref name="key"/>
        /// (<paramref name="layerHits"/>), appending the refinement to <paramref name="updates"/>: a small candidate is
        /// refined and copied into the frame; a large one goes through the node's child cache under selector 0 (every
        /// layer).
        /// </summary>
        /// <returns><c>false</c> if no row survives.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        bool RefineScanHits(FrameUpdates updates, ref Prober prober, SingleScanSpec scan, uint key, LayerHits layerHits)
        {
            if (layerHits.Total <= SmallRefinement)
            {
                var refined = RefineHits(ref prober, layerHits, scan.Constraints);
                if (refined.IsEmpty)
                {
                    refined.Dispose(pool.Layers);
                    return false;
                }

                updates.RefineAtomLayered(scan.Atom, refined, pool);
                return true;
            }

            var node = prober.Node;
            var arity = prober.First.View!.Table.Arity;
            var slot = scan.Column.Index;
            if (node.TryGetChild(slot, key, out var child) == false)
                child = node.AddChild(slot, key, pool.Rent(RefineHits(ref prober, layerHits, scan.Constraints)), arity, pool);

            if (child.IsEmpty)
                return false;

            updates.RefineAtom(scan.Atom, child);
            return true;
        }

        /// <summary>
        /// Probes <paramref name="prober"/> with the key that <paramref name="probe"/> builds from the tuple at
        /// <paramref name="source"/>[<paramref name="offset"/>] and refines the probed atom to the rows holding it, over
        /// every layer: a Free Join probe or a materialization scan's probe, whose results are not cached on trie nodes, as
        /// in egglog. One piece takes egglog's path; several join their hits across layers.
        /// </summary>
        /// <returns><c>false</c> on a miss or when no row survives; the caller rolls the frame back.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ProbeRefine(FrameUpdates updates, ref Prober prober, Probe probe, uint[] source, int offset, int cur)
        {
            if (prober.PieceCount == 1)
                return ProbeTuple(in prober.First, probe, source, offset, out var rows) && RefineProbe(updates, in prober.First, probe, rows);

            return ProbeRefineLayered(updates, ref prober, probe, source, offset, cur);
        }

        /// <summary>
        /// The several-piece half of <see cref="ProbeRefine"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        bool ProbeRefineLayered(FrameUpdates updates, ref Prober prober, Probe probe, uint[] source, int offset, int cur)
        {
            var layerHits = TakeHits(cur);
            var key = probe.Key;
            bool found;
            if (key.Length == 1)
            {
                found = ProbeAll(ref prober, source[offset + key[0].Index], layerHits);
            }
            else
            {
                var scratchKey = keyScratch;
                for (var k = 0; k < key.Length; k++)
                    scratchKey[k] = source[offset + key[k].Index];

                found = ProbeAll(ref prober, scratchKey, layerHits);
            }

            return found && RefineProbeHits(updates, ref prober, probe, layerHits);
        }

        /// <summary>
        /// Refines a probed atom to <paramref name="rows"/> of one piece's layer, appending the refinement to
        /// <paramref name="updates"/>. The probes of Free Join stages and materialization scans are not cached on trie
        /// nodes, as in egglog.
        /// </summary>
        /// <param name="updates">The frame buffer.</param>
        /// <param name="piece">The piece the rows come from.</param>
        /// <param name="probe">The probe.</param>
        /// <param name="rows">The atom's rows of the layer matching the key.</param>
        /// <returns><c>false</c> if no row survives; the caller rolls the frame back.</returns>
        bool RefineProbe(FrameUpdates updates, in ProberPiece piece, Probe probe, SubsetRef rows)
        {
            var atom = probe.Scan.Atom;
            var subset = RefineVisible(rows, probe.Scan.Constraints, piece.View!, piece.MayHoldInvisible);
            if (subset.IsEmpty)
            {
                subset.Dispose();
                return false;
            }

            updates.RefineAtomSubset(atom, piece.Layer, subset, pool);
            return true;
        }

        /// <summary>
        /// Refines a probed atom to the rows of several layers in <paramref name="layerHits"/>; see
        /// <see cref="RefineProbe"/>.
        /// </summary>
        /// <returns><c>false</c> if no row survives.</returns>
        bool RefineProbeHits(FrameUpdates updates, ref Prober prober, Probe probe, LayerHits layerHits)
        {
            var refined = RefineHits(ref prober, layerHits, probe.Scan.Constraints);
            if (refined.IsEmpty)
            {
                refined.Dispose(pool.Layers);
                return false;
            }

            updates.RefineAtomLayered(probe.Scan.Atom, refined, pool);
            return true;
        }

        /// <summary>
        /// Probes every piece of <paramref name="prober"/> with the one-cell <paramref name="key"/>, recording the pieces
        /// whose layer holds it.
        /// </summary>
        /// <returns><c>true</c> if some piece holds the key.</returns>
        bool ProbeAll(ref Prober prober, uint key, LayerHits layerHits)
        {
            layerHits.Clear();
            if (ExecutorCounters.Enabled)
                counters!.NoteLayeredProbe();

            if (prober.First.TryGetSubset(key, out var rows))
                layerHits.Add(0, rows);

            var rest = prober.Rest;
            for (var i = 0; i < prober.RestCount; i++)
                if (rest![i].TryGetSubset(key, out rows))
                    layerHits.Add(i + 1, rows);

            return layerHits.Count > 0;
        }

        /// <summary>
        /// Probes every piece of <paramref name="prober"/> with the key at <paramref name="key"/>[0..arity), recording the
        /// pieces whose layer holds it.
        /// </summary>
        /// <returns><c>true</c> if some piece holds the key.</returns>
        bool ProbeAll(ref Prober prober, uint[] key, LayerHits layerHits)
        {
            layerHits.Clear();
            if (ExecutorCounters.Enabled)
                counters!.NoteLayeredProbe();

            if (prober.First.TryGetSubset(key, out var rows))
                layerHits.Add(0, rows);

            var rest = prober.Rest;
            for (var i = 0; i < prober.RestCount; i++)
                if (rest![i].TryGetSubset(key, out rows))
                    layerHits.Add(i + 1, rows);

            return layerHits.Count > 0;
        }

        /// <summary>
        /// Refines the hits of a several-piece probe into one row set per layer: each hit's rows that its layer's view sees
        /// (when its piece may list invisible rows) and that satisfy <paramref name="constraints"/>. The per-layer
        /// array comes from the task's <see cref="TrieNodePool.Layers"/>.
        /// </summary>
        /// <returns>The rows, owned by the caller; empty when no row survives.</returns>
        LayeredSubset RefineHits(ref Prober prober, LayerHits layerHits, Constraint[] constraints)
        {
            if (layerHits.Count == 1)
            {
                var piece = prober.Piece(layerHits.Pieces[0]);
                return LayeredSubset.Single(piece.Layer, RefineVisible(layerHits.Rows[0], constraints, piece.View!, piece.MayHoldInvisible));
            }

            var top = 0;
            for (var h = 0; h < layerHits.Count; h++)
                top = Math.Max(top, prober.Piece(layerHits.Pieces[h]).Layer);

            var arrays = pool.Layers;
            var perLayer = arrays is not null && arrays.LayerCount > top ? arrays.Rent() : new Subset[top + 1];
            for (var h = 0; h < layerHits.Count; h++)
            {
                var piece = prober.Piece(layerHits.Pieces[h]);
                perLayer[piece.Layer] = RefineVisible(layerHits.Rows[h], constraints, piece.View!, piece.MayHoldInvisible);
            }

            return LayeredSubset.FromLayers(perLayer, arrays);
        }

        /// <summary>
        /// Probes <paramref name="piece"/> with the key that <paramref name="probe"/> builds from the tuple at
        /// <paramref name="source"/>[<paramref name="offset"/>].
        /// </summary>
        /// <param name="piece">The piece.</param>
        /// <param name="probe">The probe, whose key positions index the tuple.</param>
        /// <param name="source">The array holding the tuple.</param>
        /// <param name="offset">The index of the tuple's first cell.</param>
        /// <param name="rows">The matching rows.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ProbeTuple(in ProberPiece piece, Probe probe, uint[] source, int offset, out SubsetRef rows)
        {
            var key = probe.Key;
            if (key.Length == 1)
                return piece.TryGetSubset(source[offset + key[0].Index], out rows);

            var scratchKey = keyScratch;
            for (var k = 0; k < key.Length; k++)
                scratchKey[k] = source[offset + key[k].Index];

            return piece.TryGetSubset(scratchKey, out rows);
        }

        /// <summary>
        /// Gets position <paramref name="cur"/>'s layer hits, emptied: created on the position's first several-piece probe,
        /// with the per-position array, so a run whose probers all have one piece (every root run) allocates neither.
        /// </summary>
        LayerHits TakeHits(int cur)
        {
            if (hits.Length <= cur)
                Array.Resize(ref hits, Math.Max(cur + 1, frames.Length));

            var layerHits = hits[cur];
            if (layerHits is null)
                hits[cur] = layerHits = new LayerHits(depth + 1);
            else
                layerHits.Clear();

            return layerHits;
        }

        /// <summary>
        /// Takes a sparse column index from the task's stack.
        /// </summary>
        SparseColumnIndex RentSparse() => sparseFreeCount > 0 ? sparseFree[--sparseFreeCount] : new SparseColumnIndex();

        /// <summary>
        /// Returns a sparse column index to the task's stack.
        /// </summary>
        void ReturnSparse(SparseColumnIndex index)
        {
            if (sparseFreeCount == sparseFree.Length)
                Array.Resize(ref sparseFree, sparseFree.Length * 2);

            sparseFree[sparseFreeCount++] = index;
        }

    }

}
