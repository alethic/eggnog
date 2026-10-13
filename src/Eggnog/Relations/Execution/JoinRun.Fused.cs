using System;
using System.Numerics;

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
        /// merged, as in egglog. Scans skip invisible rows, with or without constraints (map §8.1 row 1).
        /// </para>
        /// <para>
        /// Layers (colored-contexts spec §9.5): the cover's rows are scanned layer by layer, each with the rows its layer's
        /// view hides, and a row's frame refines the cover to that row of that layer. A root's cover has one layer.
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
                var mask = node.LayerMask;
                while (mask != 0)
                {
                    var layer = BitOperations.TrailingZeroCount(mask);
                    mask &= mask - 1;
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
            var layers = node.LayerMask;
            while (layers != 0)
            {
                var layer = BitOperations.TrailingZeroCount(layers);
                layers &= layers - 1;
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
                        updates.RefineAtomDense(cover, layer, new OffsetRange(row, row + 1));
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
        /// The cover is scanned layer by layer (§9.5), and each probe joins its hits across the probed atom's layers.
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
            var layers = node.LayerMask;
            while (layers != 0)
            {
                var layer = BitOperations.TrailingZeroCount(layers);
                layers &= layers - 1;
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
                        updates.RefineAtomDense(cover, layer, new OffsetRange(row, row + 1));
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

    }

}
