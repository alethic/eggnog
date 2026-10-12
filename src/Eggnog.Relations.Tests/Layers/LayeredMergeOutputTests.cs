using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The layered merge stores the merge function's output (colored-contexts spec §6.6, I14), on every path that merges
    /// into a child: a write over a visible ancestor row, a write over an own row, a catch-up copy folded into an own row
    /// (§7.2 branch (b)), and a step-4 copy that lands on a key an ancestor holds (§7.3). The table joins bit sets
    /// (<see cref="OrMerge"/>), whose output is neither input, so storing the incoming row in place of the output loses
    /// bits; the differential's merges (min, max, union of ids) all output the incoming row whenever they change
    /// anything, so they cannot tell the two apart.
    /// </summary>
    public class LayeredMergeOutputTests
    {

        static (Store Store, TableId F, Context Root, uint[] Ids) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(new SortedWritesTable(1, 3, C(2), [C(0)], new OrMerge(1), ParallelHeuristics.Serial), name: "bits");
            var root = store.Root;
            return (store, f, root, Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray());
        }

        [Fact]
        public void AWriteOverAVisibleAncestorRowStoresTheJoin()
        {
            var (_, f, root, ids) = Setup();
            Insert(root, f, ids[0], 0b01, 0);
            root.Freeze();
            var child = root.CreateChild();

            Insert(child, f, ids[0], 0b10, 0);
            Assert.Equal(0b11u, Row(child, f, ids[0])![1]);
            Assert.Equal(0b01u, Row(root, f, ids[0])![1]);
            LookupPaths.Check(child);
            child.CheckInvariants();
        }

        [Fact]
        public void AWriteOverAnOwnRowStoresTheJoin()
        {
            var (_, f, root, ids) = Setup();
            root.Freeze();
            var child = root.CreateChild();

            Insert(child, f, ids[1], 0b001, 0);
            Insert(child, f, ids[1], 0b100, 0);
            Assert.Equal(0b101u, Row(child, f, ids[1])![1]);
            LookupPaths.Check(child);
            child.CheckInvariants();
        }

        [Fact]
        public void ACatchUpCopyUnderAnOwnKeyFoldsTheJoin()
        {
            var (_, f, root, ids) = Setup();
            Insert(root, f, ids[0], 0b001, 0);
            var child = root.CreateChild();
            Insert(child, f, ids[0], 0b010, 0);
            Assert.Equal(0b011u, Row(child, f, ids[0])![1]);

            // The writable root changes the row the child holds the key of: the catch-up copies it (branch (b)) and the
            // merge joins it into the own row.
            Insert(root, f, ids[0], 0b100, 0);
            Assert.Equal(0b101u, Row(root, f, ids[0])![1]);
            child.Rebuild();
            Assert.Equal(0b111u, Row(child, f, ids[0])![1]);
            LookupPaths.Check(child);
            child.CheckInvariants();
        }

        [Fact]
        public void AStepFourCopyOntoAnAncestorKeyStoresTheJoin()
        {
            var (_, f, root, ids) = Setup();
            Insert(root, f, ids[0], 0b01, 0);
            Insert(root, f, ids[1], 0b10, 0);
            root.Freeze();
            var child = root.CreateChild();

            // ids[1] loses to ids[0] in the child: step 4 copies bits(ids[1]) as bits(ids[0]), which merges with the
            // root's visible bits(ids[0]).
            child.Union(ids[0], ids[1]);
            child.Rebuild();
            Assert.Equal(0b11u, Row(child, f, ids[0])![1]);
            Assert.Null(Row(child, f, ids[1]));
            LookupPaths.Check(child);
            child.CheckInvariants();
        }

    }

}
