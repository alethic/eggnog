using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// T5 (colored-contexts spec §9.5): probers with one piece per layer. Each piece clamps and probes its own layer's
    /// index; a prober counts its pieces' keys; a driver walks every piece (one frame per (piece, key)); a probe of several
    /// pieces joins its hits across layers.
    /// </summary>
    public class LayeredProberTests
    {

        static ColumnIndex Index(params (uint Value, int Row)[] entries)
        {
            var index = ColumnIndex.Create(1, ParallelHeuristics.Serial);
            foreach (var (value, row) in entries)
                index.AddRow(value, row);

            return index;
        }

        [Fact]
        public void EachPieceClampsAndProbesItsLayer()
        {
            var low = Index((1, 0), (1, 5), (2, 6), (3, 9));
            var high = Index((1, 2), (4, 3));
            var prober = new Prober { First = new ProberPiece { Kind = ProberKind.CachedColumn, Column = low, HasOuter = true, Outer = new OffsetRange(4, 10), Layer = 0 } };
            ref var second = ref prober.AddPiece();
            second = new ProberPiece { Kind = ProberKind.DynamicColumn, Column = high, Layer = 2 };
            Assert.Equal(2, prober.PieceCount);
            Assert.Equal(1, prober.RestCount);

            // Keys per piece: the clamped low piece holds at most one key per row of its range, the high one two.
            Assert.Equal(Math.Min(low.Count, 6) + high.Count, prober.Count);
            Assert.True(Prober.GetPiece(ref prober, 0).TryGetSubset(1, out var rows));
            Assert.Equal([5], ExecutionTestUtil.Ids(rows));
            Assert.True(Prober.GetPiece(ref prober, 1).TryGetSubset(1, out rows));
            Assert.Equal([2], ExecutionTestUtil.Ids(rows));
            Assert.False(prober.Piece(1).TryGetSubset(2, out _));
            Assert.Equal(2, prober.Piece(1).Layer);

            // A driver walks the pieces one after the other: one entry per (piece, key).
            var walked = new List<(int Piece, uint Key)>();
            for (var i = 0; i < prober.PieceCount; i++)
            {
                var cursor = default(ProberCursor);
                while (cursor.MoveNext(Prober.GetPiece(ref prober, i)))
                    walked.Add((i, cursor.Key));
            }

            Assert.Equal([(0, 1u), (0, 2u), (0, 3u), (1, 1u), (1, 4u)], walked.OrderBy(w => w.Piece).ThenBy(w => w.Key));
        }

        [Fact]
        public void AFrameUpdateCarriesItsLayerInTwentyFourBytes()
        {
            // §9.5: the layer byte sits beside the kind byte, so the frame buffer's entries do not grow.
            Assert.Equal(24, Unsafe.SizeOf<FrameUpdate>());
            var updates = new FrameUpdates();
            updates.RefineAtomDense(new AtomId(3), 2, new OffsetRange(5, 9));
            var e = updates.Entries[0];
            Assert.Equal(FrameUpdateKind.RefineAtomDense, e.Kind);
            Assert.Equal(2, e.Layer);
            Assert.Equal((5, 9), (e.A, e.B));

            // A refinement that lies in one layer becomes a dense or single-layer one; several layers make an owned node.
            var pool = new TrieNodePool(false, 3);
            updates.RefineAtomLayered(new AtomId(1), LayeredSubset.Single(1, Subset.Dense(0, 4)), pool);
            Assert.Equal(FrameUpdateKind.RefineAtomDense, updates.Entries[1].Kind);
            Assert.Equal(1, updates.Entries[1].Layer);
            var layers = pool.Layers!.Rent();
            layers[0] = Subset.Dense(0, 2);
            layers[2] = Subset.Dense(3, 5);
            updates.RefineAtomLayered(new AtomId(1), LayeredSubset.FromLayers(layers, pool.Layers), pool);
            Assert.Equal(FrameUpdateKind.RefineAtomOwned, updates.Entries[2].Kind);
            Assert.True(updates.Entries[2].Node!.IsMultiLayer);
            updates.Rollback(pool);
        }

        [Fact]
        public void AddPieceReusesTheSpareArray()
        {
            var prober = default(Prober);
            for (var i = 0; i < 5; i++)
                prober.AddPiece().Layer = (byte)(i + 1);

            var rest = prober.Rest;
            Assert.Equal(6, prober.PieceCount);
            prober.RestCount = 0;
            prober.AddPiece();
            Assert.Same(rest, prober.Rest);
            Assert.Equal(0, prober.Rest![0].Layer);
        }

        /// <summary>
        /// Without key uniqueness (S6 off), a key visible in two layers of the driving atom gives two frames, one per
        /// layer, which partition the atom's rows with that key; the probed atom's hits in two layers join as one row set.
        /// The distinct bindings are the flat join's (I27); the count may exceed it (decision 6).
        /// </summary>
        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        public void DriversSplitByLayerAndProbesJoinAcrossLayers(string strategyName)
        {
            var levels = new Database[2];
            for (var d = 0; d < levels.Length; d++)
            {
                var level = new Database(ParallelHeuristics.Serial);
                level.AddTable(new SortedWritesTable(2, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
                level.AddTable(new SortedWritesTable(1, 1, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
                levels[d] = level;
            }

            var a = new TableId(0);
            var b = new TableId(1);

            // a(x, y): (1, 10) in both layers, (2, 20) in layer 1; b(y): 10 in layer 0, 20 in layer 1.
            DatabaseTestUtil.Stage(levels[0], a, [1, 10]);
            DatabaseTestUtil.Stage(levels[1], a, [1, 10], [2, 20]);
            DatabaseTestUtil.Stage(levels[0], b, [10]);
            DatabaseTestUtil.Stage(levels[1], b, [20]);
            foreach (var level in levels)
                level.MergeAll();

            var layers = new SyntheticLayers(levels);
            layers.Define(a, [true, true], new List<int>?[2], new List<int>?[2]);
            layers.Define(b, [true, true], new List<int>?[2], new List<int>?[2]);
            layers.Seal();
            var db = levels[0];
            db.AttachView(layers);

            var log = new List<uint[]>();
            var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.Strategy = Enum.Parse<PlanStrategy>(strategyName);
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(a, [x, y]);
            q.AddAtom(b, [y]);
            var rb = q.Build();
            rb.CallExternal(recorder, [x, y]);
            rb.Build("a and b");
            using var rs = rsb.Build();
            var report = db.RunRuleSet(rs);

            Assert.Equal(["1,10", "2,20"], ExecutionTestUtil.Distinct(log));
            Assert.Equal(3, report.NumMatches("a and b"));
            Assert.Equal(3, log.Count);
        }

    }

}
