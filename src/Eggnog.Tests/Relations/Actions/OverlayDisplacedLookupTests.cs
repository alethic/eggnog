using System;
using System.Collections.Generic;

using Xunit;

using static Eggnog.Relations.Tests.ActionTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="OverlayDisplacedLookup"/> and the <see cref="TableKind.OverlayDisplaced"/> arm of
    /// <see cref="VectorizedLookup"/>: a child's <c>lookup_uf</c> answers column 1 for every id with the child's
    /// canonical, and columns 0 and 2 for the ids displaced in the child's view.
    /// </summary>
    public class OverlayDisplacedLookupTests
    {

        static int[] Lanes(Mask mask)
        {
            var lanes = new List<int>();
            foreach (var lane in mask)
                lanes.Add(lane);

            return lanes.ToArray();
        }

        /// <summary>
        /// The root displaces 3 into 1; the child merges 5 with 1 (5 loses, at timestamp 7).
        /// </summary>
        static OverlayDisplacedTable Child()
        {
            var root = new DisplacedTable();
            TableTestUtil.Stage(root, [3, 1, 2]);
            root.Merge(TableTestUtil.EmptyState());
            var child = new OverlayDisplacedTable(new LayeredUnionFind(root.UnionFind), new UnionFindOverlay(), new DisplacedJournal(), root);
            TableTestUtil.Stage(child, [5, 3, 7]);
            child.Merge(TableTestUtil.EmptyState());
            return child;
        }

        [Fact]
        public void ColumnOneAnswersEveryIdWithTheChildsCanonical()
        {
            var child = Child();
            var b = Batch((0, [1, 3, 5, 9, 1000]));
            var mask = new Mask(5);
            VectorizedLookup.LookupRow(child, ref mask, b, [V(0)], C(1), V(1));
            Assert.Equal(new uint[] { 1, 1, 1, 9, 1000 }, Column(b, 1));
            Assert.Equal(5, mask.Count);

            // The producer directly, lane by lane.
            var lookup = new OverlayDisplacedLookup(child, ArgSource.Of(V(0), b), 1);
            Assert.True(lookup.TryProduce(2, out var value));
            Assert.Equal(1u, value);
        }

        [Fact]
        public void ColumnsZeroAndTwoExistForIdsDisplacedInTheView()
        {
            var child = Child();
            var b = Batch((0, [1, 3, 5, 9, 1000]));

            // 3 is displaced at the root (timestamp 2), 5 in the child (timestamp 7); 1, 9 and 1000 are canonical.
            var displaced = new Mask(5);
            VectorizedLookup.LookupRow(child, ref displaced, b, [V(0)], C(2), V(2));
            Assert.Equal(new[] { 1, 2 }, Lanes(displaced));
            Assert.Equal(new uint[] { S, 2, 7, S, S }, Column(b, 2));

            var defaulted = new Mask(5);
            VectorizedLookup.LookupWithDefault(child, ref defaulted, b, [V(0)], C(0), V(0), V(3));
            Assert.Equal(new uint[] { 1, 3, 5, 9, 1000 }, Column(b, 3));
            Assert.Equal(5, defaulted.Count);

            Assert.Throws<ArgumentException>(() =>
            {
                var m = new Mask(5);
                VectorizedLookup.LookupRow(child, ref m, b, [V(0), V(0)], C(1), V(4));
            });
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var m = new Mask(5);
                VectorizedLookup.LookupRow(child, ref m, b, [V(0)], C(3), V(4));
            });
        }

    }

}
