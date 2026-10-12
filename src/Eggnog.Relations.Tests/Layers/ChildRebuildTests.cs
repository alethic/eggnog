using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for one rebuild pass of a child (colored-contexts spec §7.3, C5, X8, X9, decision 3): losers only, epochs,
    /// congruence through the layered merge, step 4 examining exactly the uses of the own losers, the rows written since
    /// the last rebuild, own rewrites merged (R2-3), and the incremental path on a large own layer.
    /// </summary>
    public class ChildRebuildTests
    {

        /// <summary>
        /// Only rows naming the loser are copied (decision 3): the root's rows naming the winner stay read through.
        /// </summary>
        [Fact]
        public void OnlyRowsNamingTheLoserAreCopied()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(Relation(2), name: "g");
            var root = store.Root;
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            var (a, b, c, d) = (ids[0], ids[1], ids[2], ids[3]);
            Insert(root, g, a, c, 0);
            Insert(root, g, b, d, 0);
            root.Freeze();

            var child = root.CreateChild();
            child.Union(a, b);
            child.Rebuild();
            Assert.True(child.IsClean);

            var statistics = child.Tables!.Statistics;
            Assert.Equal(1, statistics.AncestorRowsExamined);
            Assert.Equal(1, statistics.RowsCopied);

            // g(a, c) is still the root's row, read through; g(b, d) is hidden and its copy g(a, d) is the child's own.
            var view = child.GetView(g);
            Assert.True(view.FindRow([a, c], out var layer) >= 0);
            Assert.Equal(0, layer);
            Assert.True(view.FindRow([a, d], out layer) >= 0);
            Assert.Equal(1, layer);
            Assert.True(view.FindRow([b, d], out _) < 0);
            Assert.Equal(2, view.VisibleCount);
            LookupPaths.Check(child, [[b, d], [a, d]]);

            // The root is unchanged.
            Assert.NotNull(Row(root, g, b, d));
            Assert.Null(Row(root, g, a, d));
        }

        /// <summary>
        /// Copies and rewrites carry the epoch of the pass that made them (I3, I19): at least the child's creation stamp,
        /// below the clock after the rebuild.
        /// </summary>
        [Fact]
        public void CopiesAndRewritesCarryThePassEpoch()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            Insert(root, f, ids[1], 3, 0);
            root.Freeze();

            var child = root.CreateChild();
            Insert(child, f, ids[3], 4, 0);
            child.Union(ids[0], ids[1]);
            child.Union(ids[2], ids[3]);
            var before = store.ReadClock();
            child.Rebuild();
            var after = store.ReadClock();

            var copied = Row(child, f, ids[0])!;
            var rewritten = Row(child, f, ids[2])!;
            Assert.Equal(3u, copied[1]);
            Assert.Equal(4u, rewritten[1]);
            foreach (var row in new[] { copied, rewritten })
            {
                Assert.InRange(row[2], before, after - 1);
                Assert.True(row[2] >= child.CreatedAt);
            }

            // The journal's stamps never decrease (I3).
            var stamps = child.Journal!.Timestamps.ToArray();
            Assert.True(stamps.SequenceEqual(stamps.OrderBy(t => t)));
        }

        /// <summary>
        /// Congruence through C4: a copy that lands on a key the root already holds goes through the merge function, whose
        /// union is staged in the child; the next pass applies it.
        /// </summary>
        [Fact]
        public void CongruenceRunsThroughTheLayeredMerge()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var pair = store.AddTable(Constructor(store, 2), [], [store.UnionFindTable], "pair");
            var root = store.Root;
            var ids = Enumerable.Range(0, 6).Select(_ => root.NewId()).ToArray();
            var (one, two, three, five, six) = (ids[1], ids[2], ids[3], ids[4], ids[5]);
            Insert(root, pair, one, two, five, 0);
            Insert(root, pair, three, two, six, 0);
            root.Freeze();

            // The spec's worked trace (§7.3): C unions 3 with 1; pair(3,2)=6 is hidden and copied as pair(1,2)=6, which
            // hits the root's pair(1,2)=5; UnionIdMerge stages union(5,6) in C and stores nothing.
            var child = root.CreateChild();
            child.Union(one, three);
            child.Rebuild();
            Assert.Equal(child.Find(five), child.Find(six));
            Assert.Equal(five, child.Find(six));
            Assert.Equal(1, child.GetView(pair).VisibleCount);
            Assert.Equal(five, Row(child, pair, one, two)![2]);
            Assert.Null(Row(child, pair, three, two));

            // The root never learns either equality.
            Assert.NotEqual(root.Find(five), root.Find(six));
            Assert.NotEqual(root.Find(one), root.Find(three));
        }

        /// <summary>
        /// Step 4 examines exactly the uses of the own losers, through the root's rebuild index, never a scan.
        /// </summary>
        [Fact]
        public void AncestorRowsExaminedEqualsTheUsesOfTheLosers()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(Relation(2), name: "g");
            var h = store.AddTable(MaxFunction(1), name: "h");
            var root = store.Root;
            var ids = Enumerable.Range(0, 40).Select(_ => root.NewId()).ToArray();
            var loser = ids[39];
            root.Write(state =>
            {
                for (var i = 0; i < 30; i++)
                    state.StageInsert(g, [ids[i], ids[i + 1], state.Timestamp]);

                for (var i = 0; i < 5; i++)
                    state.StageInsert(g, [loser, ids[i], state.Timestamp]);

                state.StageInsert(g, [ids[7], loser, state.Timestamp]);
                state.StageInsert(h, [loser, 1, state.Timestamp]);
                state.StageInsert(h, [ids[3], 2, state.Timestamp]);
                return 0;
            });
            root.Freeze();

            var child = root.CreateChild();
            child.Union(ids[38], loser);
            child.Rebuild();

            // Seven uses: five g(loser, _), one g(_, loser), one h(loser).
            var statistics = child.Tables!.Statistics;
            Assert.Equal(7, statistics.AncestorRowsExamined);
            Assert.Equal(7, statistics.RowsCopied);
            Assert.Equal(0, statistics.CatchUpRowsExamined);
            Assert.Equal(36, child.GetView(g).VisibleCount);
            Assert.Equal(1u, Row(child, h, ids[38])![1]);
            LookupPaths.Check(child);
        }

        /// <summary>
        /// The own rows written since the layer's last rebuild are rewritten with the rest: a row written before a union and
        /// one written after the previous rebuild both end canonical.
        /// </summary>
        [Fact]
        public void RowsWrittenSinceTheLastRebuildAreRewritten()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            root.Freeze();
            var child = root.CreateChild();
            var ids = Enumerable.Range(0, 8).Select(_ => child.NewId()).ToArray();
            for (var i = 0; i < 4; i++)
                Insert(child, f, ids[i], (uint)i, 0);

            child.Union(ids[0], ids[1]);
            child.Rebuild();
            var layer = child.Tables!.TryGetLayer(f)!.Own;
            Assert.True(layer.LastRebuildWasIncremental);

            // Written after that rebuild, then made stale by a union: rewritten by the next pass.
            Insert(child, f, ids[5], 9, 0);
            Insert(child, f, ids[6], 8, 0);
            child.Union(ids[4], ids[5]);
            child.Union(ids[2], ids[6]);
            child.Rebuild();
            Assert.True(child.IsClean);
            Assert.Equal(9u, Row(child, f, ids[4])![1]);
            Assert.Equal(8u, Row(child, f, ids[2])![1]);
            Assert.Null(Row(child, f, ids[5]));
            Assert.Null(Row(child, f, ids[6]));
            Assert.Equal(1u, Row(child, f, ids[0])![1]);
            Assert.Equal(4, child.GetView(f).VisibleCount);
            child.CheckInvariants();
        }

        /// <summary>
        /// Own rewrites are merged (R2-3): C unions 3 into 1, its own f(3) becomes f(1), and C is clean and canonical.
        /// </summary>
        [Fact]
        public void OwnRewritesAreMerged()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, f, ids[3], 7, 0);
            child.Union(ids[1], ids[3]);
            Assert.False(child.IsClean);
            child.Rebuild();

            Assert.True(child.IsClean);
            Assert.Equal(7u, Row(child, f, ids[1])![1]);
            Assert.Null(Row(child, f, ids[3]));
            var own = child.Tables!.TryGetLayer(f)!.Own;
            Assert.Equal(1, own.Count);
            child.CheckInvariants();
            LookupPaths.Check(child, [[ids[3]], [ids[1]]]);
        }

        /// <summary>
        /// The layered incremental override (§7.3, R2-12): on an own layer of 5000 rows, a union whose loser one row names
        /// rewrites through the rebuild index, examining only that row, where egglog's rule (more than 10 000 rows) would
        /// rescan the layer.
        /// </summary>
        [Fact]
        public void TheIncrementalPathOnALargeLayerExaminesOnlyTheRowsNamingTheLoser()
        {
            const int Rows = 5000;
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            root.Freeze();
            var child = root.CreateChild();
            var ids = child.Write(state =>
            {
                var minted = new uint[Rows + 1];
                for (var i = 0; i <= Rows; i++)
                    minted[i] = (uint)state.IncrementCounter(store.IdCounter);

                for (var i = 0; i < Rows; i++)
                    state.StageInsert(f, [minted[i], 1, state.Timestamp]);

                return minted;
            });

            // A first rebuild sets the layer's rebuild mark.
            child.Union(ids[Rows], ids[Rows - 1]);
            child.Rebuild();
            var own = child.Tables!.TryGetLayer(f)!.Own;
            Assert.True(own.LastRebuildWasIncremental);

            // The loser ids[7] is named by one own row.
            child.Union(ids[3], ids[7]);
            child.Rebuild();
            Assert.True(own.LastRebuildWasIncremental);
            Assert.Equal(1, own.LastRebuildRows);
            Assert.Null(Row(child, f, ids[7]));
            Assert.Equal(1u, Row(child, f, ids[3])![1]);
            Assert.Equal(Rows - 1, child.GetView(f).VisibleCount);

            // The root's rule would have taken the full path on 5000 rows.
            Assert.False(SortedWritesTable.ShouldRebuildIncrementally(1, Rows, false));
        }

        /// <summary>
        /// A child under a parent that is not frozen: the parent's own union and the child's own union in one rebuild of
        /// the child (imports and own losers together), against a copy.
        /// </summary>
        [Fact]
        public void ImportsAndOwnLosersInOneRebuild()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 6).Select(_ => root.NewId()).ToArray();
            for (var i = 0; i < ids.Length; i++)
                Insert(root, f, ids[i], (uint)(10 + i), 0);

            var child = root.CreateChild();
            Insert(child, f, ids[4], 50, 0);
            child.Union(ids[2], ids[3]);
            root.Union(ids[0], ids[4]);
            child.Rebuild();

            Assert.True(child.IsClean);
            Assert.Equal(50u, Row(child, f, ids[0])![1]);
            Assert.Equal(13u, Row(child, f, ids[2])![1]);
            Assert.Null(Row(child, f, ids[3]));
            Assert.Null(Row(child, f, ids[4]));
            Assert.Equal(14u, Row(root, f, ids[0])![1]);
            Assert.Equal(12u, Row(root, f, ids[2])![1]);
            child.CheckInvariants();
            LookupPaths.Check(child, ids.Select(id => new[] { id }));
        }

    }

}
