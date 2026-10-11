using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="StageOrdering"/>: egglog's <c>sort_plan_by_size</c>, <c>recompute_leaf_scans</c>,
    /// <c>estimate_size</c> and <c>num_intersected_rels</c> (core-relations/src/free_join/execute.rs:2266-2505).
    /// </summary>
    public class StageOrderingTests
    {

        static IntersectStage Isect(int variable, params int[] atoms) =>
            new(new Variable(variable), atoms.Select(a => new SingleScanSpec(new AtomId(a), new ColumnId(0), [])).ToArray());

        static FusedIntersectStage Scan(int atom, int variable, params Probe[] probes) =>
            new(new ScanSpec(new SubAtom(new AtomId(atom), [new ColumnId(0)]), []), [new StageBinding(new ColumnId(0), new Variable(variable))], probes);

        static Probe ProbeOf(int atom, int columns) =>
            new(new ScanSpec(new SubAtom(new AtomId(atom), Enumerable.Range(0, columns).Select(c => new ColumnId(c)).ToArray()), []), new ColumnId[columns]);

        static FusedIntersectMatStage Mat(int mat, MatScanMode mode, int[] modeVars, int[] bindVars, params Probe[] probes) =>
            new(new MatId(mat), mode, modeVars.Select(v => new Variable(v)).ToArray(), bindVars.Select((v, i) => new StageBinding(new ColumnId(i), new Variable(v))).ToArray(), probes);

        static int[] Sort(JoinStage[] stages, InstrOrder order, int start, FixedSizes sizes, bool[]? leafScans = null)
        {
            leafScans ??= new bool[stages.Length];
            StageOrdering.SortPlanBySize(order, leafScans, start, stages, ref sizes, new long[16]);
            return order.ToArray();
        }

        [Fact]
        public void SmallerStagesRunFirst()
        {
            JoinStage[] stages = [Isect(0, 0, 1), Isect(1, 2, 3), Scan(4, 2)];
            var order = Sort(stages, new InstrOrder(3), 0, new FixedSizes([50, 40, 30, 20, 10]));
            Assert.Equal([2, 1, 0], order);
        }

        [Fact]
        public void RefinedAtomsComeBeforeSmallerOnes()
        {
            // After the first pick (the smallest, which refines a0), a stage touching a0 again outranks a smaller one.
            JoinStage[] stages = [Isect(0, 0, 1), Isect(1, 2, 3), Isect(2, 0, 4)];
            var order = Sort(stages, new InstrOrder(3), 0, new FixedSizes([100, 1, 30, 30, 100]));
            Assert.Equal([0, 2, 1], order);
        }

        [Fact]
        public void TiesPreferMoreRelations()
        {
            JoinStage[] stages = [Isect(0, 0, 1), Isect(1, 2, 3, 4)];
            var order = Sort(stages, new InstrOrder(2), 0, new FixedSizes([10, 10, 10, 10, 10]));
            Assert.Equal([1, 0], order);
        }

        [Fact]
        public void BarriersDoNotMove()
        {
            // Value and Lookup read earlier bindings, so stages never cross them; each segment sorts on its own.
            JoinStage[] stages =
            [
                Isect(0, 0, 1), Scan(2, 1),
                Mat(0, MatScanMode.Value, [0], [5]),
                Isect(2, 3, 4), Scan(5, 3),
                Mat(1, MatScanMode.Lookup, [3], []),
                Scan(6, 4), Scan(7, 6),
            ];
            var order = Sort(stages, new InstrOrder(stages.Length), 0, new FixedSizes([9, 9, 1, 9, 9, 1, 5, 2], [3, 3]));
            Assert.Equal([1, 0, 2, 4, 3, 5, 7, 6], order);
        }

        [Fact]
        public void KeyOnlyCommutes()
        {
            // A KeyOnly prologue is not a barrier: with equal refinement, a smaller intersection moves before it.
            JoinStage[] stages = [Scan(1, 0), Mat(0, MatScanMode.KeyOnly, [], [1], ProbeOf(0, 1)), Isect(2, 1, 2)];
            var order = Sort(stages, new InstrOrder(3), 1, new FixedSizes([5, 1, 5], [7]));
            Assert.Equal([0, 2, 1], order);
        }

        [Fact]
        public void PositionsBeforeStartStay()
        {
            JoinStage[] stages = [Isect(0, 0, 1), Isect(1, 2, 3), Scan(4, 2)];
            var order = Sort(stages, new InstrOrder(3), 1, new FixedSizes([50, 40, 30, 20, 10]));
            Assert.Equal([0, 2, 1], order);
        }

        [Fact]
        public void RefinementCountsFollowTheScheduledOrder()
        {
            // Position 0 already runs stage 1 (a scan of a1). egglog seeds the counts from plan index 0 (a scan of a0),
            // so it would next prefer stage 3 (on a0); the stages actually run refined a1, so stage 2 (on a1) goes next
            // (map §8.1 row 18).
            JoinStage[] stages = [Scan(0, 0), Scan(1, 1), Isect(2, 1, 2), Isect(3, 0, 3)];
            var order = new InstrOrder(4);
            order.Swap(0, 1);
            Assert.Equal([1, 2, 3, 0], Sort(stages, order, 1, new FixedSizes([10, 10, 10, 10])));
        }

        [Fact]
        public void EstimatesAndRelationCounts()
        {
            var sizes = new FixedSizes([7, 3, 9], [4]);
            Assert.Equal(3, StageOrdering.EstimateSize(Isect(0, 0, 1, 2), ref sizes));
            Assert.Equal(9, StageOrdering.EstimateSize(Scan(2, 0), ref sizes));
            Assert.Equal(4, StageOrdering.EstimateSize(Mat(0, MatScanMode.Full, [], [0]), ref sizes));
            Assert.Equal(3, StageOrdering.NumIntersectedRelations(Isect(0, 0, 1, 2)));
            Assert.Equal(2, StageOrdering.NumIntersectedRelations(Scan(2, 0, ProbeOf(1, 1))));
            Assert.Equal(1, StageOrdering.NumIntersectedRelations(Mat(0, MatScanMode.KeyOnly, [], [0], ProbeOf(1, 1))));
        }

        [Fact]
        public void LeafScansAreProbeFreeScansNothingLaterDependsOn()
        {
            JoinStage[] stages = [Isect(0, 0, 1), Scan(1, 1), Scan(2, 2)];
            var leaf = new bool[3];
            var order = new InstrOrder(3);
            StageOrdering.RecomputeLeafScans(order, leaf, stages, 0);
            Assert.Equal([false, true, true], leaf);

            // Scheduled before the intersection that refines a1, the scan of a1 is no longer a leaf.
            order.Swap(0, 1);
            StageOrdering.RecomputeLeafScans(order, leaf, stages, 0);
            Assert.Equal([false, false, true], leaf);

            // A Full scan whose binding a later Value key reads is not a leaf; Lookups never are.
            JoinStage[] result = [Mat(1, MatScanMode.Full, [], [0, 1]), Mat(0, MatScanMode.Value, [1], [2]), Mat(2, MatScanMode.Lookup, [2], [])];
            var flags = new bool[3];
            StageOrdering.RecomputeLeafScans(new InstrOrder(3), flags, result, 0);
            Assert.Equal([false, false, false], flags);
            StageOrdering.RecomputeLeafScans(new InstrOrder(2), flags, result[..2], 0);
            Assert.Equal([false, true], flags[..2]);

            // A scan probed by a later stage is not a leaf; a KeyOnly with probes never is.
            JoinStage[] probed = [Scan(0, 0), Scan(1, 1, ProbeOf(0, 1)), Mat(0, MatScanMode.KeyOnly, [], [2], ProbeOf(3, 1))];
            StageOrdering.RecomputeLeafScans(new InstrOrder(3), flags, probed, 0);
            Assert.Equal([false, false, false], flags);
        }

        [Fact]
        public void SortRecomputesLeafScansFromStart()
        {
            JoinStage[] stages = [Scan(1, 1), Isect(0, 0, 1), Scan(2, 2)];
            var leaf = new bool[3] { true, true, false };
            var order = Sort(stages, new InstrOrder(3), 0, new FixedSizes([5, 5, 1]), leaf);
            Assert.Equal([2, 1, 0], order);
            Assert.Equal([true, false, true], leaf);
        }

        [Fact]
        public void BadArgumentsThrow()
        {
            JoinStage[] stages = [Scan(0, 0)];
            var sizes = new FixedSizes([1]);
            Assert.Throws<ArgumentException>(() => StageOrdering.SortPlanBySize(new InstrOrder(2), new bool[2], 0, stages, ref sizes, new long[1]));
            Assert.Throws<ArgumentException>(() => StageOrdering.SortPlanBySize(new InstrOrder(1), [], 0, stages, ref sizes, new long[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => StageOrdering.SortPlanBySize(new InstrOrder(1), new bool[1], 2, stages, ref sizes, new long[1]));
        }

        [Fact]
        public void SortingIsAPermutationThatKeepsBarriers()
        {
            var rng = new Random(5);
            for (var iteration = 0; iteration < 500; iteration++)
            {
                var n = rng.Next(1, 12);
                var stages = new JoinStage[n];
                for (var i = 0; i < n; i++)
                {
                    stages[i] = rng.Next(5) switch
                    {
                        0 => Isect(i, rng.Next(6), rng.Next(6)),
                        1 => Scan(rng.Next(6), i),
                        2 => Mat(rng.Next(3), MatScanMode.KeyOnly, [], [i], ProbeOf(rng.Next(6), 1)),
                        3 => Mat(rng.Next(3), MatScanMode.Value, [rng.Next(n)], [i]),
                        _ => Scan(rng.Next(6), i, ProbeOf(rng.Next(6), 2)),
                    };
                }

                var sizes = new FixedSizes(Enumerable.Range(0, 6).Select(_ => rng.Next(100)).ToArray(), Enumerable.Range(0, 3).Select(_ => rng.Next(100)).ToArray());
                var start = rng.Next(n + 1);
                var order = Sort(stages, new InstrOrder(n), start, sizes);
                Assert.Equal(Enumerable.Range(0, n), order.Order());
                for (var p = 0; p < start; p++)
                    Assert.Equal(p, order[p]);
                for (var p = start; p < n; p++)
                    if (stages[p] is FusedIntersectMatStage { IsBarrier: true })
                        Assert.Equal(p, order[p]);
            }
        }

    }

}
