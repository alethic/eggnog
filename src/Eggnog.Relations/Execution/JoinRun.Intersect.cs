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
        /// One scan: every value of the column, each a frame refining the atom to its rows.
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
            var table = tables[atom];
            var variable = stage.Variable;
            var updates = TakeFrames(cur);
            var cursor = default(ProberCursor);
            while (cursor.MoveNext(p))
            {
                var key = cursor.Key;
                updates.PushBinding(variable, key);
                if (RefineScan(updates, p, scan, key, cursor.Rows, table) == false)
                {
                    updates.Rollback(pool);
                    continue;
                }

                updates.FinishFrame();
                if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                    return;
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
            var smallTable = tables[smallScan.Atom.Index];
            var largeTable = tables[largeScan.Atom.Index];
            var variable = stage.Variable;
            var updates = TakeFrames(cur);
            var cursor = default(ProberCursor);
            while (cursor.MoveNext(smaller))
            {
                var key = cursor.Key;
                if (larger.TryGetSubset(key, out var largeRows) == false)
                    continue;

                updates.PushBinding(variable, key);
                if (RefineScan(updates, smaller, smallScan, key, cursor.Rows, smallTable) == false ||
                    RefineScan(updates, larger, largeScan, key, largeRows, largeTable) == false)
                {
                    updates.Rollback(pool);
                    continue;
                }

                updates.FinishFrame();
                if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                    return;
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
                var mainTable = tables[main.Atom.Index];
                var variable = stage.Variable;
                var updates = TakeFrames(cur);
                var cursor = default(ProberCursor);
                while (cursor.MoveNext(ps[smallest]))
                {
                    var key = cursor.Key;
                    updates.PushBinding(variable, key);
                    var ok = true;
                    for (var i = 0; i < n && ok; i++)
                    {
                        if (i == smallest)
                            continue;

                        ok = ps[i].TryGetSubset(key, out var rows) && RefineScan(updates, ps[i], scans[i], key, rows, tables[scans[i].Atom.Index]);
                    }

                    if (ok == false || RefineScan(updates, ps[smallest], main, key, cursor.Rows, mainTable) == false)
                    {
                        updates.Rollback(pool);
                        continue;
                    }

                    updates.FinishFrame();
                    if (updates.Frames >= chunk && Drain(cur, ref updates, ref sink) == false)
                        return;
                }

                if (Drain(cur, ref updates, ref sink) == false)
                    return;
            }

            for (var i = 0; i < n; i++)
                MoveBack(ref ps[i], scans[i].Atom.Index);
        }

    }

}
