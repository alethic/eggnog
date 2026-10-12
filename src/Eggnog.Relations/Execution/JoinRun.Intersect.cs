using System;

namespace Eggnog.Relations
{

    internal sealed partial class JoinRun
    {

        /// <summary>
        /// Runs an <see cref="IntersectStage"/>: a Generic Join step binding one variable to every value all its scans
        /// share. egglog's <c>JoinStage::Intersect</c> kernel (core-relations/src/free_join/execute.rs:1111-1375), with
        /// separate loops for one, two and more scans.
        /// </summary>
        /// <remarks>
        /// Layers (colored-contexts spec §9.5): the driving scan enumerates each piece of its prober in turn, one frame per
        /// (piece, key), refining its atom to that layer's rows; the probed scans join their hits across layers. A key
        /// present in several layers of the driver yields several frames, which partition the driver's rows with that key
        /// (I27; decision 6 allows the multiplicity).
        /// </remarks>
        void RunIntersect<TSink>(int cur, IntersectStage stage, int chunk, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            switch (stage.Scans.Length)
            {
                case 0:
                    return;
                case 1:
                    RunIntersectOne(cur, stage, chunk, ref sink);
                    return;
                case 2:
                    RunIntersectTwo(cur, stage, chunk, ref sink);
                    return;
                default:
                    RunIntersectMany(cur, stage, chunk, ref sink);
                    return;
            }
        }

        /// <summary>
        /// One scan: every value of the column, each a frame refining the atom to its rows of one layer.
        /// </summary>
        void RunIntersectOne<TSink>(int cur, IntersectStage stage, int chunk, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            var scan = stage.Scans[0];
            var atom = scan.Atom.Index;
            if (nodes[atom]!.IsEmpty)
                return;

            var ps = TakeProbers(cur, 1);
            ref var p = ref ps[0];
            GetIndex(ref p, atom, [scan.Column]);
            if (ExecutorCounters.Enabled)
                NoteDriver(atom, p);

            var variable = stage.Variable;
            var updates = TakeFrames(cur);
            var pieces = p.PieceCount;
            for (var i = 0; i < pieces; i++)
            {
                ref var piece = ref Prober.GetPiece(ref p, i);
                var cursor = default(ProberCursor);
                while (cursor.MoveNext(piece))
                {
                    var key = cursor.Key;
                    updates.PushBinding(variable, key);
                    if (RefineScan(updates, p.Node, piece, scan, key, cursor.Rows) == false)
                    {
                        updates.Rollback(pool);
                        continue;
                    }

                    updates.FinishFrame();
                    if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                        return;
                }
            }

            if (Drain(cur, ref updates, ref sink) == false)
                return;

            MoveBack(ref p, atom);
        }

        /// <summary>
        /// Two scans: iterate the side with fewer keys (the second on a tie) and probe the other.
        /// </summary>
        void RunIntersectTwo<TSink>(int cur, IntersectStage stage, int chunk, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            var a = stage.Scans[0];
            var b = stage.Scans[1];
            var ps = TakeProbers(cur, 2);
            GetIndex(ref ps[0], a.Atom.Index, [a.Column]);
            GetIndex(ref ps[1], b.Atom.Index, [b.Column]);
            var small = ps[0].Count < ps[1].Count ? 0 : 1;
            var large = 1 - small;
            ref var smaller = ref ps[small];
            ref var larger = ref ps[large];
            var smallScan = stage.Scans[small];
            var largeScan = stage.Scans[large];
            if (ExecutorCounters.Enabled)
                NoteDriver(smallScan.Atom.Index, smaller);

            var layerHits = larger.PieceCount != 1 ? TakeHits(cur) : null;
            var variable = stage.Variable;
            var updates = TakeFrames(cur);
            var pieces = smaller.PieceCount;
            for (var i = 0; i < pieces; i++)
            {
                ref var piece = ref Prober.GetPiece(ref smaller, i);
                var cursor = default(ProberCursor);
                while (cursor.MoveNext(piece))
                {
                    var key = cursor.Key;
                    if (layerHits is not null)
                    {
                        if (ProbeAll(ref larger, key, layerHits) == false)
                            continue;

                        updates.PushBinding(variable, key);
                        if (RefineScan(updates, smaller.Node, piece, smallScan, key, cursor.Rows) == false ||
                            RefineScanHits(updates, ref larger, largeScan, key, layerHits) == false)
                        {
                            updates.Rollback(pool);
                            continue;
                        }
                    }
                    else
                    {
                        if (larger.First.TryGetSubset(key, out var largeRows) == false)
                            continue;

                        updates.PushBinding(variable, key);
                        if (RefineScan(updates, smaller.Node, piece, smallScan, key, cursor.Rows) == false ||
                            RefineScan(updates, larger.Node, larger.First, largeScan, key, largeRows) == false)
                        {
                            updates.Rollback(pool);
                            continue;
                        }
                    }

                    updates.FinishFrame();
                    if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                        return;
                }
            }

            if (Drain(cur, ref updates, ref sink) == false)
                return;

            MoveBack(ref ps[0], a.Atom.Index);
            MoveBack(ref ps[1], b.Atom.Index);
        }

        /// <summary>
        /// Three or more scans: iterate the scan with the fewest keys (the first on a tie) and probe every other; a miss
        /// discards the candidate. The driving scan is refined last, as in egglog.
        /// </summary>
        void RunIntersectMany<TSink>(int cur, IntersectStage stage, int chunk, ref TSink sink)
            where TSink : struct, IJoinSink
        {
            var scans = stage.Scans;
            var n = scans.Length;
            var ps = TakeProbers(cur, n);
            var smallest = 0;
            var smallestSize = int.MaxValue;
            for (var i = 0; i < n; i++)
            {
                GetIndex(ref ps[i], scans[i].Atom.Index, [scans[i].Column]);
                var size = ps[i].Count;
                if (size < smallestSize)
                {
                    smallest = i;
                    smallestSize = size;
                }
            }

            if (smallestSize != 0)
            {
                var main = scans[smallest];
                var variable = stage.Variable;
                var updates = TakeFrames(cur);
                ref var driver = ref ps[smallest];
                if (ExecutorCounters.Enabled)
                    NoteDriver(main.Atom.Index, driver);

                var pieces = driver.PieceCount;
                for (var pi = 0; pi < pieces; pi++)
                {
                    ref var piece = ref Prober.GetPiece(ref driver, pi);
                    var cursor = default(ProberCursor);
                    while (cursor.MoveNext(piece))
                    {
                        var key = cursor.Key;
                        updates.PushBinding(variable, key);
                        var ok = true;
                        for (var i = 0; i < n && ok; i++)
                        {
                            if (i == smallest)
                                continue;

                            ok = ProbeRefineScan(updates, ref ps[i], scans[i], key, cur);
                        }

                        if (ok == false || RefineScan(updates, driver.Node, piece, main, key, cursor.Rows) == false)
                        {
                            updates.Rollback(pool);
                            continue;
                        }

                        updates.FinishFrame();
                        if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                            return;
                    }
                }

                if (Drain(cur, ref updates, ref sink) == false)
                    return;
            }

            for (var i = 0; i < n; i++)
                MoveBack(ref ps[i], scans[i].Atom.Index);
        }

    }

}
