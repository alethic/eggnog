using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Run-time reordering of a block's stages by current sizes: egglog's <c>sort_plan_by_size</c>,
    /// <c>recompute_leaf_scans</c>, <c>estimate_size</c> and <c>num_intersected_rels</c>
    /// (core-relations/src/free_join/execute.rs:2266-2505).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The static stage order is only a starting point: the executor sorts the stages of a block at its start and again
    /// at some depths of the search, using the sizes of the current row sets (after headers such as semi-naive deltas).
    /// Stages commute except materialization barriers (<see cref="FusedIntersectMatStage.IsBarrier"/>), so the stages
    /// between barriers are sorted independently and barriers never move.
    /// </para>
    /// <para>
    /// Within a segment a selection sort picks, at each position, the stage with the smallest key
    /// (<c>-refined</c>, <c>estimated size</c>, <c>-relations</c>): stages touching atoms already refined most, then the
    /// smallest, then those joining the most relations.
    /// </para>
    /// <para>
    /// Fix (map §8.1 row 18, execute.rs:2413): egglog seeds the refinement counts from the stages at plan indexes below
    /// the segment, not from the stages actually scheduled there. Here they are seeded from the scheduled stages
    /// (positions below the segment in the current order). Performance only; any order is correct.
    /// </para>
    /// <para>
    /// No allocation: the caller supplies the refinement-count scratch array. Sizes come from a struct type argument.
    /// </para>
    /// </remarks>
    internal static class StageOrdering
    {

        /// <summary>
        /// Estimates how many values or rows a stage enumerates. egglog's <c>estimate_size</c>: the smallest scanned atom
        /// for an <see cref="IntersectStage"/>, the cover atom for a <see cref="FusedIntersectStage"/>, the key count for
        /// a <see cref="FusedIntersectMatStage"/>.
        /// </summary>
        /// <typeparam name="TSizes">The size source.</typeparam>
        /// <param name="stage">The stage.</param>
        /// <param name="sizes">The current sizes.</param>
        /// <returns>The estimate.</returns>
        public static long EstimateSize<TSizes>(JoinStage stage, ref TSizes sizes)
            where TSizes : struct, IStageSizeSource
        {
            switch (stage.Kind)
            {
                case JoinStageKind.Intersect:
                    {
                        var scans = Unsafe.As<IntersectStage>(stage).Scans;
                        long min = long.MaxValue;
                        for (var i = 0; i < scans.Length; i++)
                            min = Math.Min(min, sizes.GetAtomSize(scans[i].Atom));

                        return scans.Length == 0 ? 0 : min;
                    }
                case JoinStageKind.FusedIntersect:
                    return sizes.GetAtomSize(Unsafe.As<FusedIntersectStage>(stage).Cover.Atom);
                default:
                    return sizes.GetMaterializationSize(Unsafe.As<FusedIntersectMatStage>(stage).Cover);
            }
        }

        /// <summary>
        /// Counts the relations a stage joins. egglog's <c>num_intersected_rels</c>.
        /// </summary>
        /// <param name="stage">The stage.</param>
        /// <returns>The scans of an intersection, the probes plus the cover of a Free Join step, the probes of a
        /// materialization scan.</returns>
        public static int NumIntersectedRelations(JoinStage stage)
        {
            switch (stage.Kind)
            {
                case JoinStageKind.Intersect:
                    return Unsafe.As<IntersectStage>(stage).Scans.Length;
                case JoinStageKind.FusedIntersect:
                    return Unsafe.As<FusedIntersectStage>(stage).ToIntersect.Length + 1;
                default:
                    return Unsafe.As<FusedIntersectMatStage>(stage).ToIntersect.Length;
            }
        }

        /// <summary>
        /// Sorts the stages at positions <c>[start, Count)</c> of <paramref name="order"/> by current size, segment by
        /// segment between barriers, then recomputes <paramref name="leafScans"/> from <paramref name="start"/> on.
        /// egglog's <c>sort_plan_by_size</c>.
        /// </summary>
        /// <typeparam name="TSizes">The size source.</typeparam>
        /// <param name="order">The order; permuted.</param>
        /// <param name="leafScans">Per position, whether the stage there can run as a leaf scan; at least
        /// <c>order.Count</c> long; updated from <paramref name="start"/> on.</param>
        /// <param name="start">The first position that may move.</param>
        /// <param name="instrs">The block's stages.</param>
        /// <param name="sizes">The current sizes.</param>
        /// <param name="timesRefined">Scratch, one entry per atom index of the plan.</param>
        public static void SortPlanBySize<TSizes>(InstrOrder order, bool[] leafScans, int start, JoinStage[] instrs, ref TSizes sizes, long[] timesRefined)
            where TSizes : struct, IStageSizeSource
        {
            ArgumentNullException.ThrowIfNull(order);
            ArgumentNullException.ThrowIfNull(leafScans);
            ArgumentNullException.ThrowIfNull(instrs);
            ArgumentNullException.ThrowIfNull(timesRefined);
            if (order.Count != instrs.Length || leafScans.Length < order.Count)
                throw new ArgumentException("The order, the leaf-scan flags and the stage list must agree in length.");
            if ((uint)start > (uint)order.Count)
                throw new ArgumentOutOfRangeException(nameof(start));

            var lastPos = start;
            for (var i = start; i < order.Count; i++)
            {
                // Barriers don't commute: sort each segment between them on its own. A barrier stays at its position.
                var stage = instrs[order[i]];
                if (stage.Kind == JoinStageKind.FusedIntersectMat && Unsafe.As<FusedIntersectMatStage>(stage).IsBarrier)
                {
                    SortSegment(order, lastPos, i, instrs, ref sizes, timesRefined);
                    lastPos = i + 1;
                }
            }

            SortSegment(order, lastPos, order.Count, instrs, ref sizes, timesRefined);
            RecomputeLeafScans(order, leafScans, instrs, start);
        }

        /// <summary>
        /// Recomputes the leaf-scan flag of every position from <paramref name="start"/> on. egglog's
        /// <c>recompute_leaf_scans</c>: a position is a leaf scan when its stage is a probe-free
        /// <see cref="FusedIntersectStage"/>, or a probe-free <see cref="FusedIntersectMatStage"/> in mode
        /// <see cref="MatScanMode.Full"/>, <see cref="MatScanMode.KeyOnly"/> or <see cref="MatScanMode.Value"/>, and no
        /// later stage scans or probes its cover atom or reads one of its bound variables through a
        /// <see cref="MatScanMode.Value"/> or <see cref="MatScanMode.Lookup"/> key. A leaf scan's rows can be projected
        /// once and expanded only when matches reach the action.
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="leafScans">Per position, the flags; updated from <paramref name="start"/> on.</param>
        /// <param name="instrs">The block's stages.</param>
        /// <param name="start">The first position to recompute.</param>
        public static void RecomputeLeafScans(InstrOrder order, bool[] leafScans, JoinStage[] instrs, int start)
        {
            ArgumentNullException.ThrowIfNull(order);
            ArgumentNullException.ThrowIfNull(leafScans);
            ArgumentNullException.ThrowIfNull(instrs);
            if (leafScans.Length < order.Count)
                throw new ArgumentException("Need one leaf-scan flag per position.", nameof(leafScans));

            for (var i = start; i < order.Count; i++)
            {
                var stage = instrs[order[i]];
                var coverAtom = -1;
                StageBinding[] bind;
                if (stage.Kind == JoinStageKind.FusedIntersect && Unsafe.As<FusedIntersectStage>(stage).ToIntersect.Length == 0)
                {
                    var f = Unsafe.As<FusedIntersectStage>(stage);
                    coverAtom = f.Cover.Atom.Index;
                    bind = f.Bind;
                }
                else if (stage.Kind == JoinStageKind.FusedIntersectMat && Unsafe.As<FusedIntersectMatStage>(stage) is { ToIntersect.Length: 0, Mode: MatScanMode.Full or MatScanMode.KeyOnly or MatScanMode.Value } m)
                {
                    bind = m.Bind;
                }
                else
                {
                    leafScans[i] = false;
                    continue;
                }

                leafScans[i] = IsBlocked(order, instrs, i + 1, coverAtom, bind) == false;
            }
        }

        static bool IsBlocked(InstrOrder order, JoinStage[] instrs, int from, int coverAtom, StageBinding[] bind)
        {
            for (var j = from; j < order.Count; j++)
            {
                var later = instrs[order[j]];
                switch (later.Kind)
                {
                    case JoinStageKind.Intersect:
                        if (coverAtom >= 0)
                            foreach (var scan in Unsafe.As<IntersectStage>(later).Scans)
                                if (scan.Atom.Index == coverAtom)
                                    return true;
                        break;
                    case JoinStageKind.FusedIntersect:
                        if (coverAtom >= 0)
                        {
                            var f = Unsafe.As<FusedIntersectStage>(later);
                            if (f.Cover.Atom.Index == coverAtom || ProbesAtom(f.ToIntersect, coverAtom))
                                return true;
                        }
                        break;
                    default:
                        var m = Unsafe.As<FusedIntersectMatStage>(later);
                        if (coverAtom >= 0 && ProbesAtom(m.ToIntersect, coverAtom))
                            return true;
                        if (m.Mode is MatScanMode.Value or MatScanMode.Lookup)
                            foreach (var v in m.ModeVariables)
                                foreach (var b in bind)
                                    if (b.Variable == v)
                                        return true;
                        break;
                }
            }

            return false;
        }

        static bool ProbesAtom(Probe[] probes, int atom)
        {
            foreach (var p in probes)
                if (p.Scan.Atom.Index == atom)
                    return true;

            return false;
        }

        /// <summary>
        /// Selection-sorts positions <c>[from, to)</c>. egglog's <c>sort_plan_by_size_inner</c>.
        /// </summary>
        static void SortSegment<TSizes>(InstrOrder order, int from, int to, JoinStage[] instrs, ref TSizes sizes, long[] timesRefined)
            where TSizes : struct, IStageSizeSource
        {
            // Nothing to sort with zero or one stage.
            if (to - from <= 1)
                return;

            // How many times each atom has been intersected by the stages scheduled before the segment.
            Array.Clear(timesRefined);
            for (var p = 0; p < from; p++)
                CountRefinements(instrs[order[p]], timesRefined);

            for (var i = from; i < to; i++)
            {
                var stage = instrs[order[i]];
                var refineI = Refinement(stage, timesRefined);
                var sizeI = EstimateSize(stage, ref sizes);
                var relsI = NumIntersectedRelations(stage);
                for (var j = i + 1; j < to; j++)
                {
                    var other = instrs[order[j]];
                    var refineJ = Refinement(other, timesRefined);
                    var sizeJ = EstimateSize(other, ref sizes);
                    var relsJ = NumIntersectedRelations(other);

                    // Key (-refine, size, -relations), compared lexicographically.
                    if (refineJ > refineI || (refineJ == refineI && (sizeJ < sizeI || (sizeJ == sizeI && relsJ > relsI))))
                    {
                        order.Swap(i, j);
                        refineI = refineJ;
                        sizeI = sizeJ;
                        relsI = relsJ;
                    }
                }

                CountRefinements(instrs[order[i]], timesRefined);
            }
        }

        static long Refinement(JoinStage stage, long[] timesRefined)
        {
            switch (stage.Kind)
            {
                case JoinStageKind.Intersect:
                    {
                        var scans = Unsafe.As<IntersectStage>(stage).Scans;
                        var max = long.MinValue;
                        for (var i = 0; i < scans.Length; i++)
                            max = Math.Max(max, timesRefined[scans[i].Atom.Index]);

                        return max;
                    }
                case JoinStageKind.FusedIntersect:
                    return timesRefined[Unsafe.As<FusedIntersectStage>(stage).Cover.Atom.Index];
                default:
                    return Unsafe.As<FusedIntersectMatStage>(stage).Bind.Length;
            }
        }

        static void CountRefinements(JoinStage stage, long[] timesRefined)
        {
            switch (stage.Kind)
            {
                case JoinStageKind.Intersect:
                    foreach (var scan in Unsafe.As<IntersectStage>(stage).Scans)
                        timesRefined[scan.Atom.Index]++;
                    break;
                case JoinStageKind.FusedIntersect:
                    {
                        var f = Unsafe.As<FusedIntersectStage>(stage);
                        timesRefined[f.Cover.Atom.Index] += f.Cover.ToIndex.Columns.Length;
                        foreach (var p in f.ToIntersect)
                            timesRefined[p.Scan.Atom.Index] += p.Scan.ToIndex.Columns.Length;
                        break;
                    }
                default:
                    foreach (var p in Unsafe.As<FusedIntersectMatStage>(stage).ToIntersect)
                        timesRefined[p.Scan.Atom.Index] += p.Scan.ToIndex.Columns.Length;
                    break;
            }
        }

    }

}
