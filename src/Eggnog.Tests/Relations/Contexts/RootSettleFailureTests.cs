using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Failures the root's settle records, when it settles to freeze or to create a child: a stale raw write whose
    /// canonical key collides under an asserting merge.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class RootSettleFailureTests
    {

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="output">The test output.</param>
        public RootSettleFailureTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Freeze refuses a failure recorded and not taken, also one its own settle records: the root never freezes
        /// with an untaken failure shared by its descendants.
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
            Insert(root, g, b, 2, 0);   // raw, stale: b lost to a; stored as given
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
        /// The same settle at CreateChild, which succeeds; the test reports whether the settle's failure stays recorded
        /// on the root while the child exists, and asserts nothing about it.
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

    }

}
