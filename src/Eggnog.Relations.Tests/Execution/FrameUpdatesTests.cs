using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The frame buffer protocol of egglog's <c>FrameUpdates</c> (core-relations/src/free_join/frame_update.rs).
    /// </summary>
    public class FrameUpdatesTests
    {

        static FrameUpdateKind[] Kinds(FrameUpdates updates) => updates.Entries.Take(updates.Count).Select(e => e.Kind).ToArray();

        [Fact]
        public void RollbackDiscardsOnlyTheUnfinishedFrame()
        {
            var pool = new TrieNodePool(false);
            var updates = new FrameUpdates();
            updates.PushBinding(new Variable(0), 7);
            updates.RefineAtomDense(new AtomId(1), new OffsetRange(3, 4));
            updates.FinishFrame();
            updates.PushBinding(new Variable(0), 8);
            updates.Rollback(pool);
            updates.Rollback(pool);
            Assert.Equal(1, updates.Frames);
            Assert.Equal([FrameUpdateKind.PushBinding, FrameUpdateKind.RefineAtomDense, FrameUpdateKind.EndFrame], Kinds(updates));
            Assert.Equal(7, updates.Entries[0].A);
            Assert.Equal((3, 4), (updates.Entries[1].A, updates.Entries[1].B));
        }

        [Fact]
        public void DenseSubsetsNeedNoNode()
        {
            var pool = new TrieNodePool(false);
            var updates = new FrameUpdates();
            updates.RefineAtomSubset(new AtomId(0), Subset.Dense(2, 9), pool);
            Assert.Equal(FrameUpdateKind.RefineAtomDense, updates.Entries[0].Kind);
            Assert.Null(updates.Entries[0].Node);
        }

        [Fact]
        public void SparseSubsetsGetAnOwnedNodeThatRollbackReleases()
        {
            var pool = new TrieNodePool(false);
            var updates = new FrameUpdates();
            var sparse = Subset.Empty;
            sparse.AddRowSorted(1);
            sparse.AddRowSorted(5);
            updates.RefineAtomSubset(new AtomId(2), sparse, pool);
            var entry = updates.Entries[0];
            Assert.Equal(FrameUpdateKind.RefineAtomOwned, entry.Kind);
            Assert.Equal(2, entry.Id);
            Assert.Equal([1, 5], ExecutionTestUtil.Ids(entry.Node!.Rows));
            updates.Rollback(pool);
            Assert.Equal(0, updates.Count);

            // The released node is the next one the pool hands out.
            Assert.Same(entry.Node, pool.Rent(Subset.Dense(0, 1)));
        }

        [Fact]
        public void SharedNodesAreNotReleasedByRollback()
        {
            var pool = new TrieNodePool(false);
            var node = pool.Rent(Subset.Dense(0, 3));
            var updates = new FrameUpdates();
            updates.RefineAtom(new AtomId(0), node);
            updates.Rollback(pool);
            Assert.NotSame(node, pool.Rent(Subset.Dense(0, 1)));
            Assert.Equal(3, node.Size);
        }

        [Fact]
        public void ClearResetsEverything()
        {
            var updates = new FrameUpdates();
            for (var i = 0; i < 100; i++)
            {
                updates.PushBinding(new Variable(i % 3), (uint)i);
                updates.FinishFrame();
            }

            Assert.Equal(100, updates.Frames);
            Assert.Equal(200, updates.Count);
            updates.Clear();
            Assert.Equal(0, updates.Frames);
            Assert.Equal(0, updates.Count);
        }

    }

}
