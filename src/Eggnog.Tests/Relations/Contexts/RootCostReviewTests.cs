using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probes (root-cost lens, M9.4): per-child cost against the root's size where the root is not frozen.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class RootCostReviewTests
    {

        readonly ITestOutputHelper output;

        public RootCostReviewTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        static (Store Store, TableId F, uint[] Ids) WritableRoot(int rows)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = new uint[rows];
            for (var i = 0; i < rows; i++)
                ids[i] = root.NewId();

            root.Write(state =>
            {
                for (var i = 0; i < ids.Length; i++)
                    state.StageInsert(f, [ids[i], (uint)(i % 7), state.Timestamp]);

                return 0;
            });
            return (store, f, ids);
        }

        /// <summary>
        /// Bytes and time of one child of a writable root: one union of two root ids, then a rebuild (step 4 probes the
        /// root's rebuild index). Measured from after CreateChild, so the root's settle is excluded.
        /// </summary>
        static (long Bytes, double Ms, long Examined) ChildUnion(Store store, uint a, uint b)
        {
            var child = store.Root.CreateChild();
            GC.Collect();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();
            child.Union(a, b);
            child.Rebuild();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            watch.Stop();
            var examined = child.Tables!.Statistics.AncestorRowsExamined;
            child.Dispose();
            return (bytes, watch.Elapsed.TotalMilliseconds, examined);
        }

        [Fact]
        public void FirstChildOfAWritableRootPaysForTheRootsRebuildIndex()
        {
            var (smallStore, _, smallIds) = WritableRoot(20_000);
            var (largeStore, _, largeIds) = WritableRoot(200_000);

            var small1 = ChildUnion(smallStore, smallIds[0], smallIds[1]);
            var large1 = ChildUnion(largeStore, largeIds[0], largeIds[1]);
            var small2 = ChildUnion(smallStore, smallIds[2], smallIds[3]);
            var large2 = ChildUnion(largeStore, largeIds[2], largeIds[3]);
            output.WriteLine($"first child:  20k {small1.Bytes} B {small1.Ms:F2} ms examined {small1.Examined}; 200k {large1.Bytes} B {large1.Ms:F2} ms examined {large1.Examined}");
            output.WriteLine($"second child: 20k {small2.Bytes} B {small2.Ms:F2} ms examined {small2.Examined}; 200k {large2.Bytes} B {large2.Ms:F2} ms examined {large2.Examined}");
            Assert.Equal(small1.Examined, large1.Examined);
            Assert.True(large1.Bytes <= small1.Bytes * 3 / 2, $"first child: {small1.Bytes} B over 20k rows, {large1.Bytes} B over 200k rows");
        }

        /// <summary>
        /// Attribution: with the root's rebuild index built beforehand, the first child is flat.
        /// </summary>
        [Fact]
        public void WithTheRootsRebuildIndexPrebuiltTheFirstChildIsFlat()
        {
            var (smallStore, sf, smallIds) = WritableRoot(20_000);
            var (largeStore, lf, largeIds) = WritableRoot(200_000);
            Assert.Null(typeof(SortedWritesTable).GetField("rebuildIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(largeStore.Schema[lf]!.Root));
            smallStore.Schema[sf]!.Root.SharedRebuildIndex();
            largeStore.Schema[lf]!.Root.SharedRebuildIndex();
            var small1 = ChildUnion(smallStore, smallIds[0], smallIds[1]);
            var large1 = ChildUnion(largeStore, largeIds[0], largeIds[1]);
            output.WriteLine($"first child (index prebuilt): 20k {small1.Bytes} B {small1.Ms:F2} ms; 200k {large1.Bytes} B {large1.Ms:F2} ms");
            Assert.True(large1.Bytes <= small1.Bytes * 3 / 2, $"first child: {small1.Bytes} B over 20k rows, {large1.Bytes} B over 200k rows");
        }


        /// <summary>
        /// The deleted M9.3 case AnAncestorFoldThatOnlyTombstonesIsSeenAsItIs, under M9.4: a root rebuild that folds a
        /// rewritten row into an equal row only tombstones; the child sees it as a copy would, with and without an own row
        /// naming the loser.
        /// </summary>
        [Fact]
        public void AnAncestorFoldThatOnlyTombstonesIsSeenAsItIs()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var d = root.NewId();
            Insert(root, f, a, 1, 0);
            Insert(root, f, b, 1, 0);
            Insert(root, f, c, 1, 0);
            Insert(root, f, d, 1, 0);
            var plain = root.CreateChild();
            var holder = root.CreateChild();
            Insert(holder, f, d, 5, 0);
            root.Union(a, b);
            root.Union(c, d);
            root.Rebuild();
            plain.Rebuild();
            holder.Rebuild();
            plain.CheckInvariants();
            holder.CheckInvariants();
            Assert.Equal(a, plain.Find(b));
            Assert.Equal(1u, Row(plain, f, a)![1]);
            Assert.Equal(1u, Row(plain, f, c)![1]);
            Assert.Equal(5u, Row(holder, f, c)![1]);
            Assert.Equal(1u, Row(holder, f, a)![1]);
            LookupPaths.Check(plain);
            LookupPaths.Check(holder);
        }

        /// <summary>
        /// Freeze refuses a failure recorded and not taken (the implementer's §13 edge 22 check), but checks before its own
        /// rebuild and settle: a failure the root's settle records (a stale raw write whose canonical key collides under
        /// an asserting merge) is frozen into the shared root.
        /// </summary>
        [Fact]
        public void AFailureTheFreezesOwnSettleRecordsIsNotRefused()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(new SortedWritesTable(1, 3, C(2), [C(0)], new AssertEqualMerge(C(1), "g"), ParallelHeuristics.Serial), name: "g");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Union(a, b);
            root.Rebuild();
            Insert(root, g, a, 1, 0);
            Insert(root, g, b, 2, 0);   // raw, stale: b lost to a; stored as given (X38)
            Assert.Null(root.Database.Failure);
            var threw = false;
            try
            {
                root.Freeze();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            output.WriteLine($"threw {threw}, frozen {root.IsFrozen}, failure {root.Database.Failure ?? "<none>"}");
            Assert.False(root.IsFrozen && root.Database.Failure is not null, $"the root froze with an untaken failure: {root.Database.Failure}");
        }

        /// <summary>
        /// The same at CreateChild: the settle's failure stays recorded on the root while a child exists.
        /// </summary>
        [Fact]
        public void AFailureTheCreateChildSettleRecordsStaysPending()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(new SortedWritesTable(1, 3, C(2), [C(0)], new AssertEqualMerge(C(1), "g"), ParallelHeuristics.Serial), name: "g");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Union(a, b);
            root.Rebuild();
            Insert(root, g, a, 1, 0);
            Insert(root, g, b, 2, 0);
            var child = root.CreateChild();
            output.WriteLine($"after CreateChild: root failure {root.Database.Failure ?? "<none>"}");
        }

        /// <summary>
        /// The deleted M9.3 test InterimRowWorkTests.AnAncestorFoldThatOnlyTombstonesIsSeenAsItIs, verbatim: it asserts no
        /// interim refusal and still holds under M9.4.
        /// </summary>
        [Fact]
        public void DeletedInterimCaseStillHolds()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, f, a, 5, 0);
            Insert(root, f, b, 2, 0);
            var child = root.CreateChild();
            var version = store.Schema[f]!.Root.Version;

            root.Union(a, b);
            child.Rebuild();
            Assert.Equal(version, store.Schema[f]!.Root.Version);
            Assert.True(child.IsClean);
            Assert.Equal(5u, Row(child, f, a)![1]);
            Assert.Null(Row(child, f, b));
            Assert.Equal(5u, Row(root, f, a)![1]);
            LookupPaths.Check(child);
        }
    }

}
