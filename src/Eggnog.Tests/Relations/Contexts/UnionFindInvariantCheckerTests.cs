using System;
using System.Linq;
using System.Reflection;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the union-find part of the context invariant checker: a clean context passes, and each deliberate
    /// corruption of a context's union-find structures, made behind the engine's back through reflection or internal
    /// members, is reported with the invariant it breaks.
    /// </summary>
    public class UnionFindInvariantCheckerTests
    {

        /// <summary>
        /// Reads the private instance field <paramref name="name"/> of <paramref name="owner"/>.
        /// </summary>
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

        /// <summary>
        /// Writes the private instance field <paramref name="name"/> of <paramref name="owner"/>.
        /// </summary>
        static void SetField(object owner, string name, object? value) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);

        /// <summary>
        /// Asserts that the checker throws for <paramref name="context"/> with a message naming <paramref name="tag"/>.
        /// </summary>
        static void AssertViolation(Context context, string tag)
        {
            var error = Assert.Throws<InvalidOperationException>(() => context.CheckInvariants());
            Assert.Contains(tag, error.Message);
        }

        /// <summary>
        /// A root with ids 0..9 and the unions 1 → 0 and, after the child exists, 7 → 0; a child, rebuilt clean, that
        /// merged {2, 3} and {4, 5, 6}.
        /// </summary>
        static (Store Store, Context Root, Context Child, uint[] Ids) Setup()
        {
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 10).Select(_ => root.NewId()).ToArray();
            root.Union(ids[1], ids[0]);
            var child = root.CreateChild();
            child.Union(ids[3], ids[2]);
            child.Union(ids[5], ids[4]);
            child.Union(ids[6], ids[5]);
            root.Union(ids[7], ids[0]);
            child.Rebuild();
            root.CheckInvariants();
            child.CheckInvariants();
            return (store, root, child, ids);
        }

        /// <summary>
        /// A clean chain of three contexts passes, writable and frozen.
        /// </summary>
        [Fact]
        public void ACleanChainPasses()
        {
            var (_, root, child, ids) = Setup();
            var grandchild = child.CreateChild();
            grandchild.Union(ids[2], ids[4]);
            grandchild.Rebuild();
            grandchild.CheckInvariants();
            root.Freeze();
            child.Freeze();
            child.CheckInvariants();
            grandchild.Rebuild();
            grandchild.CheckInvariants();
            root.CheckInvariants();
        }

        /// <summary>
        /// The checker refuses a context that is not clean.
        /// </summary>
        [Fact]
        public void TheCheckerNeedsACleanContext()
        {
            var (_, root, child, ids) = Setup();
            root.Union(ids[8], ids[9]);
            Assert.Contains("clean", Assert.Throws<InvalidOperationException>(() => child.CheckInvariants()).Message);
        }

        /// <summary>
        /// A class whose canonical is not its least member is reported.
        /// </summary>
        [Fact]
        public void ACanonicalThatIsNotTheMinimumIsReported()
        {
            var (_, _, child, ids) = Setup();
            var overlay = child.Overlay!;
            Assert.True(overlay.TryGetClass(ids[4], out var slot));
            Field<uint[]>(overlay, "canonicals")[slot] = ids[6];
            AssertViolation(child, "overlay classes:");
        }

        /// <summary>
        /// A class member that is not canonical in the parent is reported.
        /// </summary>
        [Fact]
        public void AMemberNotCanonicalInTheParentIsReported()
        {
            // Replace member 3 of the class {2, 3} by 1, which the parent displaced into 0.
            var (_, _, child, ids) = Setup();
            var overlay = child.Overlay!;
            Assert.True(overlay.TryGetClass(ids[3], out var slot));
            var members = Field<uint[]?[]>(overlay, "members")[slot]!;
            var index = Array.IndexOf(members, ids[3]);
            members[index] = ids[1];
            var classOf = Field<RawHashTable<IdEntry>>(overlay, "classOf");
            var match = new IdEntryMatch(ids[3]);
            Assert.True(classOf.Remove(FxHasher.Hash(ids[3]), ref match, out _));
            classOf.InsertUnique(FxHasher.Hash(ids[1]), new IdEntry(ids[1], (uint)slot), default(IdEntryHasher));
            AssertViolation(child, "overlay members:");
        }

        /// <summary>
        /// Journal timestamps out of order are reported.
        /// </summary>
        [Fact]
        public void JournalTimestampsOutOfOrderAreReported()
        {
            var (_, _, child, _) = Setup();
            var journal = child.Journal!;
            Assert.True(journal.Count >= 2);
            var timestamps = Field<uint[]>(journal, "timestamps");
            timestamps[0] = timestamps[journal.Count - 1] + 10;
            AssertViolation(child, "journal:");
        }

        /// <summary>
        /// A journal that misses a displaced id is reported.
        /// </summary>
        [Fact]
        public void AMissingJournalRowIsReported()
        {
            // Drop the last journal row behind the context's back, keeping it clean (its rebuild and container pass
            // marks moved back with the count): the journal then misses a displaced id.
            var (_, _, child, _) = Setup();
            var journal = child.Journal!;
            var count = journal.Count;
            var last = journal.GetId(count - 1);
            var match = new IdEntryMatch(last);
            Assert.True(Field<RawHashTable<IdEntry>>(journal, "rowOf").Remove(FxHasher.Hash(last), ref match, out _));
            SetField(journal, "count", count - 1);
            SetField(child, "journalMark", count - 1);
            child.ContainerLayers!.ContainerMark = count - 1;
            Assert.True(child.IsClean);
            AssertViolation(child, "journal:");
        }

        /// <summary>
        /// A cursor into the parent's displaced ids past their count is reported.
        /// </summary>
        [Fact]
        public void ACursorOutOfBoundsIsReported()
        {
            var (_, _, child, _) = Setup();
            SetField(child, "parentDisplacedAtCreation", child.Parent!.DisplacedCount + 1);
            Assert.True(child.IsClean);
            AssertViolation(child, "cursor bounds:");
        }

        /// <summary>
        /// An id local to a context that an ancestor's overlay holds is reported.
        /// </summary>
        [Fact]
        public void ALocalIdInAnAncestorIsReported()
        {
            // The child's overlay is made to hold an id local to the grandchild.
            var (_, _, child, ids) = Setup();
            var grandchild = child.CreateChild();
            var local = grandchild.NewId();
            Assert.True(child.Overlay!.Union(ids[8], local, out _, out _));
            Assert.True(grandchild.IsClean);
            AssertViolation(grandchild, "local ids:");
        }

        /// <summary>
        /// Root union-find rows with timestamps out of order are reported.
        /// </summary>
        [Fact]
        public void ARootRowOutOfOrderIsReported()
        {
            var (store, root, _, _) = Setup();
            var timestamps = Field<uint[]>(store.RootUnionFind.UnionFind, "timestamps");
            timestamps[0] = timestamps[1] + 10;
            AssertViolation(root, "root union-find:");
        }

        /// <summary>
        /// A frozen child whose flattened map is missing is reported.
        /// </summary>
        [Fact]
        public void AFlattenedMapThatDisagreesWithTheChainIsReported()
        {
            var (_, root, child, _) = Setup();
            root.Freeze();
            child.Freeze();
            child.CheckInvariants();
            SetField(child, "flattened", null);
            AssertViolation(child, "frozen prefix:");
        }

        /// <summary>
        /// A descendant gate whose count disagrees with the live descendants is reported, at a writable and at a frozen
        /// root.
        /// </summary>
        [Fact]
        public void AGateThatMiscountsIsReported()
        {
            var (_, root, _, _) = Setup();
            root.Gate.Enter();
            AssertViolation(root, "gate:");
            root.Gate.Exit();
            root.CheckInvariants();

            // A frozen context is checked without its domain; with no creation or disposal under way the gate is still
            // compared, so a miscount is still reported.
            root.Freeze();
            root.Gate.Enter();
            AssertViolation(root, "gate:");
            root.Gate.Exit();
            root.CheckInvariants();
        }

        /// <summary>
        /// A frozen context's gate is compared only when no creation or disposal below it is under way: a miscount read
        /// while a change is under way is not reported, since the change may account for it, and the same miscount is
        /// reported once the change has ended.
        /// </summary>
        [Fact]
        public void AGateIsNotComparedWhileAChangeIsUnderWay()
        {
            var (_, root, _, _) = Setup();
            root.Freeze();
            root.Gate.BeginChange();
            root.Gate.Enter();
            root.CheckInvariants();

            root.Gate.EndChange();
            AssertViolation(root, "gate:");
            root.Gate.Exit();
            root.CheckInvariants();
        }

        /// <summary>
        /// Changes on another thread while a frozen context is checked are never reported, although each moves the gate's
        /// count away from the tree and back while it is under way: the check reads the count and the tree only between
        /// changes, whether it starts before, during or after one of them.
        /// </summary>
        [Fact]
        public void ChangesOnAnotherThreadDuringTheCheckAreNotReported()
        {
            var (_, root, _, _) = Setup();
            root.Freeze();
            var stop = 0;
            var changer = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    root.Gate.BeginChange();
                    root.Gate.Enter();
                    root.Gate.Exit();
                    root.Gate.EndChange();
                }
            });
            changer.Start();
            try
            {
                for (var i = 0; i < 2000; i++)
                    root.CheckInvariants();
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                changer.Join();
            }

            root.CheckInvariants();
        }

        /// <summary>
        /// A change that begins while the checker walks the tree, after it found no change under way, is seen when it
        /// reads the gate again: the walk and the count it read are discarded, not compared. The checker is held at the
        /// walk by the lock on the root's child list, and the change begins and moves the count while it waits. Before
        /// the walk the checker takes no lock and does not wait, and a first check on this thread has already compiled
        /// its path, so a waiting checker is waiting for the child list, past its first read of the gate.
        /// </summary>
        [Fact]
        public void AChangeThatBeginsDuringTheWalkIsNotReported()
        {
            var (_, root, _, _) = Setup();
            root.Freeze();
            root.CheckInvariants();
            var children = Field<object>(root, "children");
            Exception? error = null;
            using var started = new ManualResetEventSlim();
            var checker = new Thread(() =>
            {
                started.Set();
                try
                {
                    root.CheckInvariants();
                }
                catch (Exception e)
                {
                    error = e;
                }
            });

            Monitor.Enter(children);
            try
            {
                checker.Start();
                started.Wait();
                var spin = new SpinWait();
                var deadline = Environment.TickCount64 + 10_000;
                while ((checker.ThreadState & ThreadState.WaitSleepJoin) == 0)
                {
                    Assert.True(Environment.TickCount64 < deadline, "the checker never waited for the child list");
                    spin.SpinOnce();
                }

                root.Gate.BeginChange();
                root.Gate.Enter();
            }
            finally
            {
                Monitor.Exit(children);
            }

            checker.Join();
            Assert.Null(error);

            root.Gate.EndChange();
            AssertViolation(root, "gate:");
            root.Gate.Exit();
            root.CheckInvariants();
        }

    }

}
