using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for containers under colored contexts, storage part: the Dirty stream of the root's container log, through
    /// which children re-stamp their own rows naming a container the root rebuilt in place, cleared when the root's last
    /// descendant goes; the eq guard, which keeps a child from holding a view in which an eq-sort container names one of
    /// its own losers, since children never rebuild containers; the refusal to intern an eq-sort container in a child;
    /// and children never rebuilding containers.
    /// </summary>
    public class ContainerContextTests
    {

        /// <summary>
        /// A store with a container type of e-class ids (or of base values) registered at the root, and a table naming
        /// container ids.
        /// </summary>
        static (Store Store, TableId F) Setup(bool holdsIds = true)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            store.Root.Database.Containers.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter), holdsIds);
            return (store, f);
        }

        /// <summary>
        /// Interns <paramref name="container"/> through <paramref name="context"/>'s database.
        /// </summary>
        static uint Intern(Context context, VecContainer container) =>
            context.Database.WithExecutionState(state => state.Containers.GetOrInsert(container, state));

        /// <summary>
        /// The root rebuilds a container in place while it has a live child: the container's id goes to the Dirty
        /// stream of the root's container log, and the child's rebuild re-stamps its own row naming it and moves its
        /// cursor past it; the log is cleared with the last descendant.
        /// </summary>
        [Fact]
        public void AChildRestampsItsRowsNamingAContainerTheRootRebuiltInPlace()
        {
            var (store, f) = Setup();
            var root = store.Root;
            var log = store.ContainerLog;
            var k = root.NewId();
            var w = root.NewId();
            var v = Intern(root, new VecContainer(w));
            var child = root.CreateChild();
            Insert(child, f, v, 5, 0);
            var stamp = Row(child, f, v)![2];
            Assert.True(log.Active);
            Assert.Equal(0, log.DirtyCount);

            // The root merges w into k: the vector [w] becomes [k] under the same id, rebuilt in place.
            root.Union(k, w);
            child.Rebuild();
            Assert.True(root.Database.Containers.TryGetValue<VecContainer>(v, out var rebuilt));
            Assert.Equal(new VecContainer(k), rebuilt);
            Assert.Equal(1, log.DirtyCount);
            Assert.Equal(1, child.Tables!.CursorAt(0)!.Dirty);

            var restamped = Row(child, f, v)!;
            Assert.Equal(5u, restamped[1]);
            Assert.True(restamped[2] > stamp, "the child's own row naming the dirty container was re-stamped");
            child.CheckInvariants();

            child.Dispose();
            Assert.False(log.Active);
            Assert.Equal(0, log.DirtyCount);
            Assert.Equal(0, log.AddedCount);
        }

        /// <summary>
        /// Without a live descendant the root's container log is inactive and records nothing, neither the container
        /// interned nor the one its rebuild produces nor the dirty id: no child will read it.
        /// </summary>
        [Fact]
        public void ARootWithoutDescendantsLogsNothing()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var log = store.ContainerLog;
            var k = root.NewId();
            var w = root.NewId();
            Intern(root, new VecContainer(w));
            root.Union(k, w);
            root.Rebuild();
            Assert.False(log.Active);
            Assert.False(log.Pending);
            Assert.Equal(0, log.AddedCount);
            Assert.Equal(0, log.DirtyCount);
            root.CheckInvariants();
        }

        /// <summary>
        /// The eq guard: a child that displaces an id an eq-sort container holds is refused (and faulted); one that
        /// displaces the other id of the union is not; an import of a parent's displacement is not checked (the root
        /// renamed it).
        /// </summary>
        [Fact]
        public void AChildCannotDisplaceAnIdAnEqContainerHolds()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var x = root.NewId();
            var w = root.NewId();
            var y = root.NewId();
            Intern(root, new VecContainer(w));
            root.Freeze();

            // w loses to x: refused.
            var refused = root.CreateChild();
            refused.Union(x, w);
            Assert.Throws<NotSupportedException>(() => refused.Rebuild());
            Assert.True(refused.IsFaulted);
            refused.Dispose();

            // y loses to w: allowed.
            var allowed = root.CreateChild();
            allowed.Union(w, y);
            allowed.Rebuild();
            Assert.Equal(w, allowed.Find(y));
        }

        /// <summary>
        /// The eq guard against containers that appear afterwards: the root (not frozen) interns an eq-sort container
        /// naming an id the child displaced in an earlier rebuild. The child is not clean from then on, even though no
        /// row changed; its next rebuild checks its whole journal against the new container and refuses it (faulted),
        /// where a copy would have unioned the containers.
        /// </summary>
        [Fact]
        public void AContainerInternedAfterTheDisplacementIsCheckedAgainstIt()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var child = root.CreateChild();
            child.Union(x, y);
            child.Rebuild();
            Assert.True(child.IsClean);

            Intern(root, new VecContainer(y));
            Assert.False(child.IsClean);
            Assert.Throws<InvalidOperationException>(() => child.Find(x));
            Assert.Throws<NotSupportedException>(() => child.Rebuild());
            Assert.True(child.IsFaulted);
            child.Dispose();
        }

        /// <summary>
        /// A container the root's own container rebuild produces is new content as well: the root unions z into y, the
        /// vector [z] becomes [y], and y is a loser of the child, which is refused.
        /// </summary>
        [Fact]
        public void AContainerTheRootsRebuildProducesIsCheckedToo()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var z = root.NewId();
            Intern(root, new VecContainer(z));
            var child = root.CreateChild();
            child.Union(x, y);
            child.Rebuild();

            root.Union(y, z);
            Assert.Throws<NotSupportedException>(() => child.Rebuild());
            Assert.True(child.IsFaulted);
        }

        /// <summary>
        /// A new container that names none of a child's losers costs it one guard run: the child is not clean until it
        /// rebuilds, and that rebuild runs no pass and takes no epoch. A child with no displacement is not even made
        /// unclean.
        /// </summary>
        [Fact]
        public void AContainerNamingNoLoserOnlyRerunsTheGuard()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var z = root.NewId();
            var child = root.CreateChild();
            var untouched = root.CreateChild();
            child.Union(x, y);
            child.Rebuild();

            Intern(root, new VecContainer(z));
            Assert.False(child.IsClean);
            Assert.True(untouched.IsClean);
            var clock = store.ReadClock();
            Assert.False(child.Rebuild());
            Assert.Equal(clock, store.ReadClock());
            Assert.True(child.IsClean);
            Assert.Equal(x, child.Find(y));
            child.CheckInvariants();
        }

        /// <summary>
        /// A frozen context interns no new eq-sort container: its container log never grows, so its descendants would
        /// never learn of it. A child created before the freeze keeps the root's log active, and the refused intern
        /// records nothing in it. Looking one up, and interning one that exists or one over base values, stay allowed.
        /// </summary>
        [Fact]
        public void AFrozenContextInternsNoNewEqContainer()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var log = store.ContainerLog;
            var w = root.NewId();
            var v = Intern(root, new VecContainer(w));
            var child = root.CreateChild();
            Intern(root, new VecContainer(w, w, w));
            root.Freeze();
            Assert.True(log.Active);
            Assert.False(log.Pending);
            var drained = log.AddedCount;
            Assert.Equal(1, drained);
            Assert.Equal(v, Intern(root, new VecContainer(w)));
            Assert.Throws<InvalidOperationException>(() => Intern(root, new VecContainer(w, w)));
            Assert.False(log.Pending);
            Assert.Equal(drained, log.AddedCount);
            Assert.False(root.Database.Containers.GetEnvironment<VecContainer>().TryGetId(new VecContainer(w, w), out _));
            root.CheckInvariants();
            child.Dispose();

            var (baseStore, _) = Setup(holdsIds: false);
            baseStore.Root.Freeze();
            Intern(baseStore.Root, new VecContainer(false, 1, 2));
        }

        /// <summary>
        /// The guard checks a pass's own displacements only: a root union of a held id reaches the child as an import.
        /// </summary>
        [Fact]
        public void AnImportedDisplacementOfAHeldIdIsAllowed()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var x = root.NewId();
            var w = root.NewId();
            Intern(root, new VecContainer(w));
            var child = root.CreateChild();
            root.Union(x, w);
            child.Rebuild();
            Assert.Equal(x, child.Find(w));
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// A child may look an eq-sort container up but not intern one; containers over base values it may intern, on
        /// the shared counter, so their ids are global.
        /// </summary>
        [Fact]
        public void AChildInternsOnlyBaseValueContainers()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var w = root.NewId();
            var v = Intern(root, new VecContainer(w));
            var child = root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => Intern(child, new VecContainer(w)));
            Assert.Throws<InvalidOperationException>(() => Intern(child, new VecContainer(w, w)));
            Assert.True(child.Database.Containers.GetEnvironment<VecContainer>().TryGetId(new VecContainer(w), out var found));
            Assert.Equal(v, found);

            var (baseStore, _) = Setup(holdsIds: false);
            var baseChild = baseStore.Root.CreateChild();
            var id = Intern(baseChild, new VecContainer(false, 1, 2));
            Assert.False(baseChild.IsLocalId(id));
            Assert.Equal(id, Intern(baseStore.Root, new VecContainer(false, 1, 2)));
            Assert.False(baseStore.Root.Database.Containers.HasEqContainers);
            Assert.True(store.Root.Database.Containers.HasEqContainers);
        }

        /// <summary>
        /// Children never rebuild containers: a child's database refuses the container rebuild and egglog's pass.
        /// </summary>
        [Fact]
        public void ChildrenNeverRebuildContainers()
        {
            var (store, f) = Setup();
            var child = store.Root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => child.Database.RebuildContainers(store.UnionFindTable));
            Assert.Throws<InvalidOperationException>(() => child.Database.RebuildPass(store.UnionFindTable, [f]));
        }

        static (Store Store, TableId F) ContainerSetup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            store.Root.Database.Containers.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter), holdsIds: true);
            return (store, f);
        }

        /// <summary>
        /// A child may not hold a view in which an eq-sort container names one of its own losers, since children never
        /// rebuild containers. A container the (writable) root interns after the child's rebuild, naming the child's
        /// loser, is checked at the child's next rebuild, which refuses the child (<see cref="NotSupportedException"/>,
        /// faulted) unless the two containers are equal in it: in a fresh root replaying the chain, the containers [x]
        /// and [y] become equal once x and y are, and their ids are unioned.
        /// </summary>
        [Fact]
        public void AnEqContainerInternedByTheRootAfterAChildDisplacedItsElementIsRefused()
        {
            var (store, f) = ContainerSetup();
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var child = root.CreateChild();

            // y loses to x in the child; no container holds y yet, so the guard lets it pass.
            child.Union(x, y);
            child.Rebuild();
            Assert.Equal(x, child.Find(y));

            // The root (not frozen) interns [x] and [y] and names them in rows the child will read.
            var kx = Intern(root, new VecContainer(x));
            var ky = Intern(root, new VecContainer(y));
            Insert(root, f, kx, 1, 0);
            Insert(root, f, ky, 2, 0);

            // Sound: the child is refused (NotSupportedException, faulted), or the two containers are equal in it.
            Exception? refused = null;
            try
            {
                child.Rebuild();
            }
            catch (NotSupportedException e)
            {
                refused = e;
            }

            if (refused is null)
                Assert.True(child.AreEqual(kx, ky), $"the child equates {x} and {y} but keeps the containers [{x}] (id {kx}) and [{y}] (id {ky}) apart, and was not refused");
        }

        /// <summary>
        /// The copy the eq guard compares with: the same chain replayed in a fresh root unions the two container ids.
        /// </summary>
        [Fact]
        public void TheFreshRootCopyOfThatChainEquatesTheContainers()
        {
            var (store, f) = ContainerSetup();
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var kx = Intern(root, new VecContainer(x));
            var ky = Intern(root, new VecContainer(y));
            Insert(root, f, kx, 1, 0);
            Insert(root, f, ky, 2, 0);
            root.Union(x, y);
            root.Rebuild();
            Assert.True(root.AreEqual(kx, ky));
        }

    }

}
