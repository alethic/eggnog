using System;

namespace Eggnog.Relations
{

    internal sealed partial class JoinRun
    {

        /// <summary>
        /// Runs a <see cref="FusedIntersectStage"/> without probes: a scan of the cover atom. egglog's kernel
        /// (core-relations/src/free_join/execute.rs:1376-1455).
        /// </summary>
        /// <remarks>
        /// <para>
        /// As a leaf scan (no later stage reads the atom or the variables), the cover's projected rows become one binding
        /// set and one empty frame, so the cross product is expanded only at the action. Otherwise the cover is scanned
        /// in chunks and every row is its own frame, refining the cover to that row; duplicate projections are not
        /// merged, as in egglog. Scans skip invisible rows, with or without constraints, where egglog's constrained
        /// scans skip the tombstone check (core-relations/src/table/mod.rs:429-439).
        /// </para>
        /// <para>
        /// Layers: the cover's rows are scanned layer by layer, each with the rows its layer's view hides, and a row's
        /// frame refines the cover to that row of that layer. A root's cover has one layer. A layer past
        /// <see cref="byte.MaxValue"/> is scanned by <see cref="ScanDeepLayer"/>, whose refinements are nodes.
        /// </para>
        /// </remarks>
        void RunFusedScan<TSink>(int cur, FusedIntersectStage stage, PreparedStage info, int chunk, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            var cover = stage.Cover.Atom;
            var atom = cover.Index;
            if (nodes[atom]!.IsEmpty)
                return;

            var view = atomViews[atom]!;
            var node = nodes[atom]!;
            nodes[atom] = null;
            var variables = info.BindVariables;
            var constraints = stage.Cover.Constraints;
            if (leafScans[cur])
            {
                var rows = TakeLeafBuffer(cur, variables.Length);
                foreach (var layer in node.GetLayers())
                {
                    var lv = view.Layers[layer]!;
                    var layerRows = node.GetLayerRows(layer);
                    lv.Table.ScanProject(layerRows, info.Projection, 0, int.MaxValue, constraints, lv.Hidden, rows);
                    if (ExecutorCounters.Enabled)
                        counters!.NoteScanned(layer < depth, IsRoot(atom, node), layerRows.Size);
                }

                if (rows.IsEmpty)
                {
                    nodes[atom] = node;
                    return;
                }

                PushBindingSet(variables, rows);
                var single = TakeFrames(cur);
                single.FinishFrame();
                if (Drain(cur, ref single, ref sink) == false)
                    return;

                PopBindingSet();
                nodes[atom] = node;
                return;
            }

            var buffer = TakeScanBuffer(cur, variables.Length);
            var updates = TakeFrames(cur);
            foreach (var layer in node.GetLayers())
            {
                if (layer > byte.MaxValue)
                {
                    if (ScanDeepLayer(cur, stage, info, chunk, node, layer, buffer, ref updates, ref sink) == false)
                        return;

                    continue;
                }

                var narrow = (byte)layer;
                var lv = view.Layers[layer]!;
                var table = lv.Table;
                var hidden = lv.Hidden;
                var subset = node.GetLayerRows(layer);
                var offset = 0;
                while (true)
                {
                    buffer.Clear();
                    var next = table.ScanProject(subset, info.Projection, offset, chunk, constraints, hidden, buffer);
                    if (ExecutorCounters.Enabled)
                        counters!.NoteScanned(layer < depth, IsRoot(atom, node), (next < 0 ? subset.Size : next) - offset);

                    var data = buffer.Data;
                    var stride = buffer.Stride;
                    for (var r = 0; r < buffer.Count; r++)
                    {
                        var start = r * stride;
                        var row = (int)data[start + variables.Length];
                        updates.RefineAtomDense(cover, narrow, new OffsetRange(row, row + 1));
                        for (var k = 0; k < variables.Length; k++)
                            updates.PushBinding(variables[k], data[start + k]);

                        updates.FinishFrame();
                        if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                            return;
                    }

                    if (next < 0)
                        break;

                    offset = next;
                }
            }

            if (Drain(cur, ref updates, ref sink) == false)
                return;

            nodes[atom] = node;
        }

        /// <summary>
        /// Runs a <see cref="FusedIntersectStage"/> with probes: a Free Join step. For each row of the cover, bind its
        /// variables, then probe each index with a key built from the row; a miss, or a probe with no visible row
        /// satisfying its constraints, discards the row. egglog's kernel (core-relations/src/free_join/execute.rs:1456-1569).
        /// The cover is scanned layer by layer, and each probe joins its hits across the probed atom's layers.
        /// </summary>
        void RunFusedIntersect<TSink>(int cur, FusedIntersectStage stage, PreparedStage info, int chunk, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            var cover = stage.Cover.Atom;
            var atom = cover.Index;
            if (nodes[atom]!.IsEmpty)
                return;

            var probes = stage.ToIntersect;
            var ps = TakeProbers(cur, probes.Length);
            for (var j = 0; j < probes.Length; j++)
                GetIndex(ref ps[j], probes[j].Scan.Atom.Index, probes[j].Scan.ToIndex.Columns);

            var view = atomViews[atom]!;
            var node = nodes[atom]!;
            nodes[atom] = null;
            var variables = info.BindVariables;
            var constraints = stage.Cover.Constraints;
            var buffer = TakeScanBuffer(cur, variables.Length);
            var updates = TakeFrames(cur);
            foreach (var layer in node.GetLayers())
            {
                if (layer > byte.MaxValue)
                {
                    if (IntersectDeepLayer(cur, stage, info, chunk, node, layer, ps, buffer, ref updates, ref sink) == false)
                        return;

                    continue;
                }

                var narrow = (byte)layer;
                var lv = view.Layers[layer]!;
                var table = lv.Table;
                var hidden = lv.Hidden;
                var subset = node.GetLayerRows(layer);
                var offset = 0;
                while (true)
                {
                    buffer.Clear();
                    var next = table.ScanProject(subset, info.Projection, offset, chunk, constraints, hidden, buffer);
                    if (ExecutorCounters.Enabled)
                        counters!.NoteScanned(layer < depth, IsRoot(atom, node), (next < 0 ? subset.Size : next) - offset);

                    var data = buffer.Data;
                    var stride = buffer.Stride;
                    for (var r = 0; r < buffer.Count; r++)
                    {
                        var start = r * stride;
                        var row = (int)data[start + variables.Length];
                        updates.RefineAtomDense(cover, narrow, new OffsetRange(row, row + 1));
                        for (var k = 0; k < variables.Length; k++)
                            updates.PushBinding(variables[k], data[start + k]);

                        var ok = true;
                        for (var j = 0; j < probes.Length && ok; j++)
                            ok = ProbeRefine(updates, ref ps[j], probes[j], data, start, cur);

                        if (ok == false)
                        {
                            updates.Rollback(pool);
                            continue;
                        }

                        updates.FinishFrame();
                        if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                            return;
                    }

                    if (next < 0)
                        break;

                    offset = next;
                }
            }

            if (Drain(cur, ref updates, ref sink) == false)
                return;

            nodes[atom] = node;
            for (var j = 0; j < probes.Length; j++)
                MoveBack(ref ps[j], probes[j].Scan.Atom.Index);
        }

        /// <summary>
        /// Scans layer <paramref name="layer"/> of the cover of <see cref="RunFusedScan"/> in chunks, one frame per row,
        /// for a layer past <see cref="byte.MaxValue"/>: each row's refinement is a node the frame owns
        /// (<see cref="FrameUpdates.RefineAtomDense(AtomId, int, OffsetRange, TrieNodePool)"/>). Kept apart, so that
        /// the row loop of the other layers only writes entries.
        /// </summary>
        /// <returns><see langword="false"/> when the sink stopped the run.</returns>
        bool ScanDeepLayer<TSink>(int cur, FusedIntersectStage stage, PreparedStage info, int chunk, TrieNode node, int layer, TaggedRowBuffer buffer, ref FrameUpdates updates, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            var cover = stage.Cover.Atom;
            var lv = atomViews[cover.Index]!.Layers[layer]!;
            var variables = info.BindVariables;
            var subset = node.GetLayerRows(layer);
            var offset = 0;
            while (true)
            {
                buffer.Clear();
                var next = lv.Table.ScanProject(subset, info.Projection, offset, chunk, stage.Cover.Constraints, lv.Hidden, buffer);
                if (ExecutorCounters.Enabled)
                    counters!.NoteScanned(layer < depth, IsRoot(cover.Index, node), (next < 0 ? subset.Size : next) - offset);

                var data = buffer.Data;
                var stride = buffer.Stride;
                for (var r = 0; r < buffer.Count; r++)
                {
                    var start = r * stride;
                    var row = (int)data[start + variables.Length];
                    updates.RefineAtomDense(cover, layer, new OffsetRange(row, row + 1), pool);
                    for (var k = 0; k < variables.Length; k++)
                        updates.PushBinding(variables[k], data[start + k]);

                    updates.FinishFrame();
                    if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                        return false;
                }

                if (next < 0)
                    return true;

                offset = next;
            }
        }

        /// <summary>
        /// Scans layer <paramref name="layer"/> of the cover of <see cref="RunFusedIntersect"/> in chunks and probes
        /// each row, for a layer past <see cref="byte.MaxValue"/>: each row's refinement is a node the frame owns
        /// (<see cref="FrameUpdates.RefineAtomDense(AtomId, int, OffsetRange, TrieNodePool)"/>). Kept apart, so that
        /// the row loop of the other layers only writes entries.
        /// </summary>
        /// <returns><see langword="false"/> when the sink stopped the run.</returns>
        bool IntersectDeepLayer<TSink>(int cur, FusedIntersectStage stage, PreparedStage info, int chunk, TrieNode node, int layer, Prober[] ps, TaggedRowBuffer buffer, ref FrameUpdates updates, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            var cover = stage.Cover.Atom;
            var lv = atomViews[cover.Index]!.Layers[layer]!;
            var probes = stage.ToIntersect;
            var variables = info.BindVariables;
            var subset = node.GetLayerRows(layer);
            var offset = 0;
            while (true)
            {
                buffer.Clear();
                var next = lv.Table.ScanProject(subset, info.Projection, offset, chunk, stage.Cover.Constraints, lv.Hidden, buffer);
                if (ExecutorCounters.Enabled)
                    counters!.NoteScanned(layer < depth, IsRoot(cover.Index, node), (next < 0 ? subset.Size : next) - offset);

                var data = buffer.Data;
                var stride = buffer.Stride;
                for (var r = 0; r < buffer.Count; r++)
                {
                    var start = r * stride;
                    var row = (int)data[start + variables.Length];
                    updates.RefineAtomDense(cover, layer, new OffsetRange(row, row + 1), pool);
                    for (var k = 0; k < variables.Length; k++)
                        updates.PushBinding(variables[k], data[start + k]);

                    var ok = true;
                    for (var j = 0; j < probes.Length && ok; j++)
                        ok = ProbeRefine(updates, ref ps[j], probes[j], data, start, cur);

                    if (ok == false)
                    {
                        updates.Rollback(pool);
                        continue;
                    }

                    updates.FinishFrame();
                    if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                        return false;
                }

                if (next < 0)
                    return true;

                offset = next;
            }
        }

    }

}
