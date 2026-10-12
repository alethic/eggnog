using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for canonical and stamped writes in a child (colored-contexts spec X38, §6.6, I13, I19, I21; review finding
    /// R2-4), the child part: a write naming a stale id lands on the canonical key and merges with the row visible there,
    /// and a raw write's timestamp is replaced by the merge's epoch. (The rest, after a child's own union and rebuild,
    /// arrives with the child's rebuild pass in M9.4.)
    /// </summary>
    public class CanonicalWriteTests
    {

        [Fact]
        public void AWriteNamingAStaleIdLandsOnTheCanonicalKey()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, f, a, 1, 0);
            root.Union(a, b);
            root.Rebuild();
            root.Freeze();
            Assert.Equal(a, root.Find(b));

            var child = root.CreateChild();
            Insert(child, f, b, 5, 0);
            Assert.Equal(1, child.Tables!.Statistics.RowsCanonicalized);
            Assert.Equal(new uint[] { a, 5 }, Row(child, f, a)![..2]);
            Assert.Null(Row(child, f, b));
            Assert.Equal(1, child.GetView(f).VisibleCount);
            Assert.Equal(1, child.Tables.Statistics.RowsHidden);

            // A smaller value under the stale key merges with the visible row and changes nothing.
            Insert(child, f, b, 3, 0);
            Assert.Equal(5u, Row(child, f, a)![1]);
            Assert.Equal(1, child.GetView(f).VisibleCount);
            LookupPaths.Check(child, [[a], [b]]);
        }

        [Fact]
        public void IdValuesAreCanonicalizedToo()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(Constructor(store), [], [store.UnionFindTable], "g");
            var root = store.Root;
            var ids = Enumerable.Range(0, 3).Select(_ => root.NewId()).ToArray();
            root.Union(ids[1], ids[2]);
            root.Rebuild();
            root.Freeze();

            // g(ids[0]) = ids[2], stale for ids[1]: stored canonical.
            var child = root.CreateChild();
            Insert(child, g, ids[0], ids[2], 0);
            Assert.Equal(ids[1], Row(child, g, ids[0])![1]);
            Assert.Equal(0, child.DisplacedCount);
        }

        [Fact]
        public void ARawWriteIsStoredAtTheMergesEpoch()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var c = root.NewId();
            root.Freeze();
            var child = root.CreateChild();
            var epoch = child.Write(state =>
            {
                state.StageInsert(f, [c, 1, 0]);
                return state.Timestamp;
            });

            var row = Row(child, f, c)!;
            Assert.Equal(epoch, row[2]);
            Assert.True(row[2] >= child.CreatedAt);
            Assert.Equal(epoch, child.GetView(f).LatestStamp);
            Assert.Equal(0, child.Tables!.Statistics.RowsCanonicalized);
        }

    }

}
