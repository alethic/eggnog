using System;
using System.Diagnostics;

namespace Eggnog.Relations
{

    internal sealed partial class JoinRun
    {

        /// <summary>
        /// Runs a probe-free <see cref="FusedIntersectMatStage"/> in mode <see cref="MatScanMode.Full"/>,
        /// <see cref="MatScanMode.KeyOnly"/> or <see cref="MatScanMode.Value"/> as a leaf scan: the rows it would bind
        /// become one binding set and one empty frame. egglog's kernel (core-relations/src/free_join/execute.rs:1570-1644).
        /// </summary>
        void RunMatLeafScan<TSink>(int cur, FusedIntersectMatStage stage, PreparedStage info, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            var mat = materializations[stage.Cover.Index]!;
            var variables = info.BindVariables;
            var columns = info.Projection;
            var rows = TakeLeafBuffer(cur, variables.Length);
            var row = rowScratch;
            var keyLength = mat.KeyArity;
            switch (stage.Mode)
            {
                case MatScanMode.Full:
                    for (var g = 0; g < mat.Count; g++)
                    {
                        var key = mat.GetKey(g);
                        var group = mat.GetValues(g);
                        for (var v = 0; v < group.Count; v++)
                        {
                            var nonKey = group.GetRow(v);
                            for (var k = 0; k < columns.Length; k++)
                            {
                                var c = columns[k].Index;
                                row[k] = c < keyLength ? key[c] : nonKey[c - keyLength];
                            }

                            rows.AddRow(0, new ReadOnlySpan<uint>(row, 0, columns.Length));
                        }
                    }

                    break;
                case MatScanMode.KeyOnly:
                    for (var g = 0; g < mat.Count; g++)
                    {
                        var key = mat.GetKey(g);
                        for (var k = 0; k < columns.Length; k++)
                            row[k] = key[columns[k].Index];

                        rows.AddRow(0, new ReadOnlySpan<uint>(row, 0, columns.Length));
                    }

                    break;
                default:
                    if (LookupGroup(mat, stage.ModeVariables, out var found))
                    {
                        var group = mat.GetValues(found);
                        for (var v = 0; v < group.Count; v++)
                        {
                            var vals = group.GetRow(v);
                            for (var k = 0; k < columns.Length; k++)
                                row[k] = vals[columns[k].Index];

                            rows.AddRow(0, new ReadOnlySpan<uint>(row, 0, columns.Length));
                        }
                    }

                    break;
            }

            if (rows.IsEmpty)
                return;

            PushBindingSet(variables, rows);
            var single = TakeFrames(cur);
            single.FinishFrame();
            if (Drain(cur, ref single, ref sink) == false)
                return;

            PopBindingSet();
        }

        /// <summary>
        /// Runs a <see cref="FusedIntersectMatStage"/>: enumerates the materialization's keys
        /// (<see cref="MatScanMode.KeyOnly"/>) or rows (<see cref="MatScanMode.Full"/>, <see cref="MatScanMode.Value"/>),
        /// binding variables and probing the bag's atoms, or checks a key is present (<see cref="MatScanMode.Lookup"/>,
        /// a semijoin). egglog's kernel (core-relations/src/free_join/execute.rs:1645-1802).
        /// </summary>
        /// <remarks>
        /// egglog never drains mid-loop in modes <see cref="MatScanMode.Full"/> and <see cref="MatScanMode.KeyOnly"/>,
        /// buffering a frame for every key or row of the materialization at once; here those modes drain every morsel,
        /// as <see cref="MatScanMode.Value"/> does. The frames and their order are the same, memory stays bounded by the
        /// morsel size, and a parallel run can hand a large materialization off in morsels.
        /// </remarks>
        void RunMat<TSink>(int cur, FusedIntersectMatStage stage, PreparedStage info, int chunk, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            var mat = materializations[stage.Cover.Index]!;
            var probes = stage.ToIntersect;
            var ps = TakeProbers(cur, probes.Length);
            for (var j = 0; j < probes.Length; j++)
                GetIndex(ref ps[j], probes[j].Scan.Atom.Index, probes[j].Scan.ToIndex.Columns);

            var variables = info.BindVariables;
            var columns = info.Projection;
            var keyLength = mat.KeyArity;
            var keys = mat.KeyData;
            var updates = TakeFrames(cur);
            switch (stage.Mode)
            {
                case MatScanMode.Full:
                    for (var g = 0; g < mat.Count; g++)
                    {
                        var keyOffset = g * keyLength;
                        var group = mat.GetValues(g);
                        var data = group.Data;
                        for (var v = 0; v < group.Count; v++)
                        {
                            var valueOffset = group.RowOffset(v);
                            for (var k = 0; k < columns.Length; k++)
                            {
                                var c = columns[k].Index;
                                updates.PushBinding(variables[k], c < keyLength ? keys[keyOffset + c] : data[valueOffset + c - keyLength]);
                            }

                            if (PruneProbers(cur, updates, probes, ps, keys, keyOffset, keyLength, data, valueOffset))
                                updates.FinishFrame();
                            else
                                updates.Rollback(pool);

                            if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                                return;
                        }
                    }

                    break;
                case MatScanMode.KeyOnly:
                    for (var g = 0; g < mat.Count; g++)
                    {
                        var keyOffset = g * keyLength;
                        for (var k = 0; k < columns.Length; k++)
                            updates.PushBinding(variables[k], keys[keyOffset + columns[k].Index]);

                        if (PruneProbers(cur, updates, probes, ps, keys, keyOffset, keyLength, keys, 0))
                            updates.FinishFrame();
                        else
                            updates.Rollback(pool);

                        if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                            return;
                    }

                    break;
                case MatScanMode.Lookup:
                    // A semijoin filter: binds nothing and probes nothing (the planner never gives a lookup either).
                    if (LookupGroup(mat, stage.ModeVariables, out var hit) && mat.GetValues(hit).Count > 0)
                        updates.FinishFrame();

                    break;
                default:
                    if (LookupGroup(mat, stage.ModeVariables, out var found))
                    {
                        var group = mat.GetValues(found);
                        var data = group.Data;
                        for (var v = 0; v < group.Count; v++)
                        {
                            var valueOffset = group.RowOffset(v);
                            for (var k = 0; k < columns.Length; k++)
                                updates.PushBinding(variables[k], data[valueOffset + columns[k].Index]);

                            if (PruneProbers(cur, updates, probes, ps, data, valueOffset, -1, data, valueOffset))
                                updates.FinishFrame();
                            else
                                updates.Rollback(pool);

                            if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                                return;
                        }
                    }

                    break;
            }

            if (Drain(cur, ref updates, ref sink) == false)
                return;

            for (var j = 0; j < probes.Length; j++)
                MoveBack(ref ps[j], probes[j].Scan.Atom.Index);
        }

        /// <summary>
        /// Probes every index of a materialization scan with a key built from the current key and row, refining each
        /// probed atom. egglog's <c>prune_probers</c> (core-relations/src/free_join/execute.rs:1676-1715): a key position
        /// below the key length reads the key, the rest read the row offset by the key length; without a key
        /// (<paramref name="keyLength"/> negative) every position reads the row. A prober of several pieces joins its hits
        /// across layers (colored-contexts spec §9.5).
        /// </summary>
        /// <returns><c>false</c> on a miss or an empty refinement; the caller rolls the frame back.</returns>
        bool PruneProbers(int cur, FrameUpdates updates, Probe[] probes, Prober[] ps, uint[] keys, int keyOffset, int keyLength, uint[] row, int rowOffset)
        {
            for (var j = 0; j < probes.Length; j++)
            {
                var probe = probes[j];
                var positions = probe.Key;
                ref var prober = ref ps[j];
                if (prober.PieceCount != 1)
                {
                    if (PruneLayered(cur, updates, ref prober, probe, keys, keyOffset, keyLength, row, rowOffset) == false)
                        return false;

                    continue;
                }

                SubsetRef rows;
                bool found;
                if (positions.Length == 1)
                {
                    found = prober.First.TryGetSubset(MatCell(keys, keyOffset, keyLength, row, rowOffset, positions[0].Index), out rows);
                }
                else
                {
                    var scratchKey = keyScratch;
                    for (var k = 0; k < positions.Length; k++)
                        scratchKey[k] = MatCell(keys, keyOffset, keyLength, row, rowOffset, positions[k].Index);

                    found = prober.First.TryGetSubset(scratchKey, out rows);
                }

                if (found == false || RefineProbe(updates, prober.First, probe, rows) == false)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// One probe of <see cref="PruneProbers"/> over a prober of several pieces: probes every piece with the key and
        /// refines the probed atom to the hits of every layer.
        /// </summary>
        bool PruneLayered(int cur, FrameUpdates updates, ref Prober prober, Probe probe, uint[] keys, int keyOffset, int keyLength, uint[] row, int rowOffset)
        {
            var positions = probe.Key;
            var layerHits = TakeHits(cur);
            bool found;
            if (positions.Length == 1)
            {
                found = ProbeAll(ref prober, MatCell(keys, keyOffset, keyLength, row, rowOffset, positions[0].Index), layerHits);
            }
            else
            {
                var scratchKey = keyScratch;
                for (var k = 0; k < positions.Length; k++)
                    scratchKey[k] = MatCell(keys, keyOffset, keyLength, row, rowOffset, positions[k].Index);

                found = ProbeAll(ref prober, scratchKey, layerHits);
            }

            return found && RefineProbeHits(updates, ref prober, probe, layerHits);
        }

        /// <summary>
        /// Reads position <paramref name="column"/> of the key-then-row tuple.
        /// </summary>
        static uint MatCell(uint[] keys, int keyOffset, int keyLength, uint[] row, int rowOffset, int column)
        {
            if (keyLength < 0)
                return row[rowOffset + column];

            return column < keyLength ? keys[keyOffset + column] : row[rowOffset + column - keyLength];
        }

        /// <summary>
        /// Finds the group of the key made of the current values of <paramref name="variables"/>.
        /// </summary>
        bool LookupGroup(Materialization mat, Variable[] variables, out int group)
        {
            Debug.Assert(variables.Length == mat.KeyArity, "a materialization is looked up by its message variables");
            var key = keyScratch;
            for (var k = 0; k < variables.Length; k++)
                key[k] = values[variables[k].Index];

            return mat.TryFind(key, 0, out group);
        }

    }

}
