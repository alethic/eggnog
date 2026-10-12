using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probes for decision 1 and I22 at a store root (colored-contexts spec §6.7): a root with a live descendant
    /// must not retract a row of a shared table through any entry point. These assert the spec's behaviour.
    /// </summary>
    public class RootRetractionBypassReviewTests
    {

        /// <summary>
        /// A root with f(1)=5, a live child, and the root row still visible to the child.
        /// </summary>
        static (Store Store, TableId F, Context Child) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            Insert(store.Root, f, 1, 5, 0);
            var child = store.Root.CreateChild();
            Assert.Equal(5u, Row(child, f, 1)![1]);
            return (store, f, child);
        }

        /// <summary>
        /// <see cref="Database.NewBuffer"/> is a public write entry point of the root's database (egglog's
        /// <c>new_buffer</c>); its removals reach the root's shared table with no guard, so the root deletes a row its live
        /// child reads.
        /// </summary>
        [Fact]
        public void ARawBufferOfTheRootCannotDeleteASharedRowWhileAChildLives()
        {
            var (store, f, _) = Setup();
            var db = store.Root.Database;

            var refused = false;
            try
            {
                var buffer = db.NewBuffer(f);
                buffer.StageRemove([1u]);
                buffer.Flush();
                db.MergeAll();
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }

            // Decision 1: the root row is still there for the child.
            Assert.Equal(1, store.Schema[f]!.Root.Count);
            Assert.True(refused, "the removal was not refused");
        }

        /// <summary>
        /// What the bypass does to the child today: a tombstone leaves the root layer's version unchanged, so the child's
        /// interim catch-up check passes and the child silently loses a row it saw (context = copy).
        /// </summary>
        [Fact]
        public void AfterARawRootDeleteTheChildStillSeesTheRow()
        {
            var (store, f, child) = Setup();
            var db = store.Root.Database;
            try
            {
                var buffer = db.NewBuffer(f);
                buffer.StageRemove([1u]);
                buffer.Flush();
                db.MergeAll();
            }
            catch (InvalidOperationException)
            {
            }

            child.Rebuild();
            Assert.Equal(5u, Row(child, f, 1)?[1]);
        }

        /// <summary>
        /// The same entry point in a child under a writable parent: R1 forbids the child's delete of a shared row it sees
        /// through the parent, but a raw buffer of the child's database hides the root row (<see cref="HideReason.Retracted"/>)
        /// unchecked.
        /// </summary>
        [Fact]
        public void ARawBufferOfAChildCannotRetractUnderAWritableParent()
        {
            var (store, f, child) = Setup();
            var db = child.Database;

            // The guarded path refuses (R1); the probe is made on a sibling, since a refusal inside a write batch faults
            // the child that made it (X12).
            var sibling = store.Root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => Remove(sibling, f, 1));
            sibling.Dispose();

            var refused = false;
            try
            {
                var buffer = db.NewBuffer(f);
                buffer.StageRemove([1u]);
                buffer.Flush();
                db.MergeAll();
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }

            // NewBuffer notified the table when it created the buffer (egglog's new_buffer), so the child has a pending
            // notification and is read only once it merged it; the refused removal staged nothing.
            child.Rebuild();
            Assert.Equal(5u, Row(child, f, 1)?[1]);
            Assert.True(refused, "the retraction was not refused");
        }

        /// <summary>
        /// A state of the root's database created before the root had a child snapshots no guard
        /// (<see cref="Database.GuardForNewState"/>), so its removals after <see cref="Context.CreateChild"/> are never
        /// checked: the snapshot is sound only for states that cannot outlive a structural call, and
        /// <see cref="Database.CreateExecutionState"/> hands out such a state.
        /// </summary>
        [Fact]
        public void AStateCreatedBeforeAChildStillPassesTheGuardAfterIt()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            Insert(store.Root, f, 1, 5, 0);
            var db = store.Root.Database;
            var state = db.CreateExecutionState();
            var child = store.Root.CreateChild();
            Assert.Equal(5u, Row(child, f, 1)![1]);

            Assert.Throws<InvalidOperationException>(() => state.StageRemove(f, [1u]));
        }

    }

}
