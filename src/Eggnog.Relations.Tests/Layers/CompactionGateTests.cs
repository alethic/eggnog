using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the compaction gate (colored-contexts spec §6.8, C12, X27, I16): a shared layer never compacts while its
    /// context has live descendants, because compaction renumbers the rows their cursors and hidden sets name; it
    /// compacts again once they are gone. A context-private table is never gated.
    /// </summary>
    public class CompactionGateTests
    {

        /// <summary>
        /// Rewrites every row of <paramref name="table"/> in <paramref name="context"/> twice, tombstoning far more than
        /// half its rows, which would compact an ungated table.
        /// </summary>
        static void Churn(Context context, TableId table, uint[] ids)
        {
            for (var round = 0; round < 3; round++)
            {
                var value = (uint)(10 + round);
                context.Write(state =>
                {
                    foreach (var id in ids)
                        state.StageInsert(table, [id, value, state.Timestamp]);

                    return 0;
                });
            }
        }

        /// <summary>
        /// A shared root table does not compact while the root has a live child (its tombstones stay, its generation
        /// too), the child still catches up correctly over them, and the root compacts again once the child is gone.
        /// </summary>
        [Fact]
        public void TheRootDoesNotCompactWhileItHasLiveDescendants()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 64).Select(_ => root.NewId()).ToArray();
            var table = store.Schema[f]!.Root;
            Assert.Same(root.Gate, table.CompactionGate);

            var child = root.CreateChild();
            var generation = table.Version.Major;
            Churn(root, f, ids);
            Assert.True(table.CompactionsBlocked > 0);
            Assert.Equal(generation, table.Version.Major);
            Assert.True(table.StaleCount > table.Count);

            // The child still catches up correctly over the uncompacted rows.
            child.Rebuild();
            Assert.Equal(12u, Row(child, f, ids[5])![1]);
            child.CheckInvariants();

            // Once the child is gone the root compacts again.
            child.Dispose();
            Churn(root, f, ids);
            Assert.True(table.Version.Major > generation);
            Assert.Equal(0, table.StaleCount);
        }

        /// <summary>
        /// A child's shared layer is gated by the child's own gate: it does not compact while a grandchild lives, and
        /// does once it is gone.
        /// </summary>
        [Fact]
        public void AChildsLayerIsGatedByItsOwnDescendants()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            root.Freeze();
            var child = root.CreateChild();
            var ids = Enumerable.Range(0, 64).Select(_ => child.NewId()).ToArray();
            Insert(child, f, ids[0], 1, 0);
            var own = child.Tables!.TryGetLayer(f)!.Own;
            Assert.Same(child.Gate, own.CompactionGate);

            var grandchild = child.CreateChild();
            var generation = own.Version.Major;
            Churn(child, f, ids);
            Assert.True(own.CompactionsBlocked > 0);
            Assert.Equal(generation, own.Version.Major);
            grandchild.Rebuild();
            Assert.Equal(12u, Row(grandchild, f, ids[9])![1]);
            grandchild.CheckInvariants();

            grandchild.Dispose();
            Churn(child, f, ids);
            Assert.True(own.Version.Major > generation);
            child.CheckInvariants();
        }

        /// <summary>
        /// A context-private table has no gate: it compacts while descendants live, which never read it.
        /// </summary>
        [Fact]
        public void AContextPrivateTableIsNeverGated()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var p = store.AddTable(MaxFunction(1), name: "p", contextPrivate: true);
            var root = store.Root;
            var ids = Enumerable.Range(0, 64).Select(_ => root.NewId()).ToArray();
            var table = store.Schema[p]!.Root;
            Assert.Null(table.CompactionGate);

            var child = root.CreateChild();
            var generation = table.Version.Major;
            Churn(root, p, ids);
            Assert.Equal(0, table.CompactionsBlocked);
            Assert.True(table.Version.Major > generation);
            child.Rebuild();
            child.CheckInvariants();
        }

        /// <summary>
        /// A freeze's forced compaction (ForceRehash) is blocked by a live child, and counted.
        /// </summary>
        [Fact]
        public void AFreezeCompactsOnlyWhereTheGateAllows()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 8).Select(_ => root.NewId()).ToArray();
            var table = store.Schema[f]!.Root;
            Churn(root, f, ids);
            Assert.True(table.StaleCount > 0);

            // Fewer tombstones than MaybeRehash's threshold of 16, so only the freeze's ForceRehash compacts, and it is
            // blocked by the live child.
            var child = root.CreateChild();
            var blocked = table.CompactionsBlocked;
            root.Freeze();
            Assert.True(table.StaleCount > 0);
            Assert.Equal(blocked + 1, table.CompactionsBlocked);
            child.Rebuild();
            child.CheckInvariants();
        }

    }

}
