using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for containers under colored contexts, storage part: the Dirty stream of the root's container log, through
    /// which children re-stamp their own rows naming a container the root rebuilt in place, cleared when the root's
    /// last descendant goes; a child's rebuild of the containers its own unions displace an element of, in its
    /// container layers; the eq guard, which refuses a child that a container its writable root or parent gains later
    /// concerns, which the child's rebuild does not reconcile, a container put on an id the child claimed beside the
    /// content there that its copy does not hold included, and lets pass one the child's next container pass claims or
    /// its copy stands for, a map's whose keys collapse in another order included; the
    /// refusal to intern an eq-sort container in a child; and a child's database refusing egglog's container rebuild.
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
        /// A child that displaces an id an eq-sort container of its frozen root holds rebuilds the container: it claims
        /// the container's id, so that it sees its own copy under it and no longer the root's, and rebuilds the copy in
        /// place, as egglog's push copy would; the root keeps the original. One that displaces the other id of the
        /// union claims nothing.
        /// </summary>
        [Fact]
        public void AChildRebuildsAContainerHoldingAnIdItDisplaces()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var x = root.NewId();
            var w = root.NewId();
            var y = root.NewId();
            var v = Intern(root, new VecContainer(w));
            root.Freeze();

            // w loses to x: the child's copy of v is [x], the root's stays [w].
            var child = root.CreateChild();
            child.Union(x, w);
            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            var view = ContainerView.Of(child.Database);
            Assert.True(view.TryGetContainer(env, v, out var rebuilt));
            Assert.Equal(new VecContainer(x), rebuilt);
            Assert.True(view.TryGetId(env, new VecContainer(x), out var id));
            Assert.Equal(v, id);
            Assert.False(view.TryGetId(env, new VecContainer(w), out _));
            Assert.True(env.TryGetContainer(v, out var original));
            Assert.Equal(new VecContainer(w), original);
            Assert.True(child.ContainerLayers!.LayerAt(env.TypeId)!.Shadowed.Contains(v));
            child.CheckInvariants();
            child.Dispose();

            // y loses to w: no container holds y, so nothing is claimed.
            var other = root.CreateChild();
            other.Union(w, y);
            other.Rebuild();
            Assert.Equal(w, other.Find(y));
            Assert.True(other.ContainerLayers!.IsEmpty);
            other.CheckInvariants();
        }

        /// <summary>
        /// The eq guard against containers that appear afterwards: the root (not frozen) interns an eq-sort container
        /// naming an id the child displaced in an earlier rebuild. The child is not clean from then on, even though no
        /// row changed; its next rebuild's guard checks the new container's elements against the displacements its
        /// container pass handled, and refuses it (faulted), where a copy would have unioned the containers.
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
        /// The root (not frozen) interns a container that holds an id the child displaced and has not rebuilt since:
        /// the guard lets it pass, since the child's container pass has not handled that displacement yet, and the
        /// pass claims the new container and rebuilds it, as it would one the root held before.
        /// </summary>
        [Fact]
        public void AContainerTheRootInternsBeforeTheChildsPassIsClaimedByIt()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var k = root.NewId();
            var w = root.NewId();
            var child = root.CreateChild();
            child.Union(k, w);

            var u = Intern(root, new VecContainer(w));
            Assert.True(EqContainerGuard.IsBehind(child));
            child.Rebuild();
            Assert.False(child.IsFaulted);
            var view = ContainerView.Of(child.Database);
            Assert.True(view.TryGetContainer(env, u, out var copy));
            Assert.Equal(new VecContainer(k), copy);
            Assert.True(view.TryGetId(env, new VecContainer(k), out var id));
            Assert.Equal(u, id);
            Assert.False(view.TryGetId(env, new VecContainer(w), out _));
            Assert.True(child.ContainerLayers!.LayerAt(env.TypeId)!.Shadowed.Contains(u));
            child.CheckInvariants();
        }

        /// <summary>
        /// The root (not frozen) interns, after the child's container pass rebuilt a claimed copy, a container equal to
        /// the copy under another id: the child's view would hold [k] twice, under the claimed v and under the root's
        /// new u, and a child does not yet merge such a container with its own, so its next rebuild refuses it
        /// (faulted). A root that had made the child's union would have returned v for [k].
        /// </summary>
        [Fact]
        public void AContainerTheRootInternsEqualToAClaimedCopyIsRefused()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var k = root.NewId();
            var w = root.NewId();
            var v = Intern(root, new VecContainer(w));
            var child = root.CreateChild();
            child.Union(k, w);
            child.Rebuild();
            Assert.True(ContainerView.Of(child.Database).TryGetContainer(env, v, out var copy));
            Assert.Equal(new VecContainer(k), copy);

            var u = Intern(root, new VecContainer(k));
            Assert.NotEqual(v, u);
            Assert.False(child.IsClean);
            var refused = Assert.Throws<NotSupportedException>(() => child.Rebuild());
            Assert.Contains("does not yet merge", refused.Message);
            Assert.True(child.IsFaulted);
            child.Dispose();
        }

        /// <summary>
        /// The root (not frozen) puts content on an id the child claimed: it unions the ids of the vectors [w] and [a],
        /// so its rebuild renames [a] onto the smaller, y, which the child claimed when its union displaced w, and
        /// whose copy it holds as [k]. The guard keeps the root's entry under y, beside [w] there, and after the
        /// rebuild's passes finds that the child's view does not hold [a] under y: the child's shadow hides the root's
        /// [a], and its copy is [k]. A child does not yet adopt such content, so the rebuild refuses it (faulted), where
        /// a fresh root replaying the chain holds both under y
        /// (<see cref="TheFreshRootCopyOfTheRenameHoldsBothContents"/>).
        /// </summary>
        [Fact]
        public void AContainerTheRootRenamesOntoAClaimedIdIsRefused()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var k = root.NewId();
            var w = root.NewId();
            var a = root.NewId();
            var y = Intern(root, new VecContainer(w));
            var z = Intern(root, new VecContainer(a));
            var child = root.CreateChild();
            child.Union(k, w);
            child.Rebuild();
            Assert.True(child.ContainerLayers!.LayerAt(env.TypeId)!.Shadowed.Contains(y));
            Assert.True(ContainerView.Of(child.Database).TryGetId(env, new VecContainer(a), out var before));
            Assert.Equal(z, before);

            root.Union(y, z);
            root.Rebuild();
            Assert.True(env.TryGetId(new VecContainer(a), out var renamed));
            Assert.Equal(y, renamed);

            // The guard keeps the root's entry under the claimed y; the rebuild checks it after its passes.
            EqContainerGuard.Check(child);
            Assert.True(child.ContainerLayers.HasEntriesOnClaimedIds);
            Assert.False(child.IsClean);
            var refused = Assert.Throws<NotSupportedException>(() => child.Rebuild());
            Assert.Contains("does not yet adopt", refused.Message);
            Assert.True(child.IsFaulted);
            child.Dispose();
        }

        /// <summary>
        /// The copy the guard compares with in <see cref="AContainerTheRootRenamesOntoAClaimedIdIsRefused"/>: the same
        /// chain replayed in a fresh root holds [k] and [a] under one id, y.
        /// </summary>
        [Fact]
        public void TheFreshRootCopyOfTheRenameHoldsBothContents()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var k = root.NewId();
            var w = root.NewId();
            var a = root.NewId();
            var y = Intern(root, new VecContainer(w));
            var z = Intern(root, new VecContainer(a));
            root.Union(k, w);
            root.Union(y, z);
            root.Rebuild();
            Assert.True(env.TryGetId(new VecContainer(k), out var ofK));
            Assert.Equal(y, ofK);
            Assert.True(env.TryGetId(new VecContainer(a), out var ofA));
            Assert.Equal(y, ofA);
            Assert.Equal(y, root.Find(z));
        }

        /// <summary>
        /// The root (not frozen) rebuilds in place a container the child claimed: its union of a and w, an element the
        /// child displaced into k, makes its [w] under y [a], which its container log records under y. The guard keeps
        /// that entry, since the child claimed y, and after the rebuild's passes finds it the only content under y at
        /// the root, which the child's copy stands for: the child imports the root's union, under which a is k there,
        /// and its copy [k] is the entry's content rebuilt. The child is not refused and keeps its copy.
        /// </summary>
        [Fact]
        public void AClaimedContainerTheRootRebuildsInPlaceIsHeldByTheChildsCopy()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var k = root.NewId();
            var a = root.NewId();
            var w = root.NewId();
            var y = Intern(root, new VecContainer(w));
            var child = root.CreateChild();
            child.Union(k, w);
            child.Rebuild();
            Assert.True(child.ContainerLayers!.LayerAt(env.TypeId)!.Shadowed.Contains(y));

            root.Union(a, w);
            root.Rebuild();
            Assert.True(env.TryGetContainer(y, out var rebuilt));
            Assert.Equal(new VecContainer(a), rebuilt);

            EqContainerGuard.Check(child);
            Assert.True(child.ContainerLayers.HasEntriesOnClaimedIds);
            child.Rebuild();
            Assert.False(child.IsFaulted);
            Assert.False(child.ContainerLayers.HasEntriesOnClaimedIds);
            Assert.True(child.IsClean);
            Assert.Equal(k, child.Find(a));
            var view = ContainerView.Of(child.Database);
            Assert.True(view.TryGetContainer(env, y, out var copy));
            Assert.Equal(new VecContainer(k), copy);
            Assert.True(view.TryGetId(env, new VecContainer(k), out var id));
            Assert.Equal(y, id);
            child.CheckInvariants();
        }

        /// <summary>
        /// The guard reads a writable parent's container log as it reads the root's: under a frozen root, a grandchild
        /// claims h, the root's [u], when it displaces u into k, and holds the copy [k]; then its writable parent's
        /// union of k and m claims h4, the root's [m], and rebuilds it to [k], which the parent's container log
        /// records. The grandchild's view would hold [k] under h and under h4, so its next rebuild refuses it
        /// (faulted).
        /// </summary>
        [Fact]
        public void AContainerAWritableParentRebuildsEqualToAGrandchildsCopyIsRefused()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var k = root.NewId();
            var m = root.NewId();
            var u = root.NewId();
            var h = Intern(root, new VecContainer(u));
            var h4 = Intern(root, new VecContainer(m));
            root.Freeze();

            var parent = root.CreateChild();
            var grandchild = parent.CreateChild();
            grandchild.Union(k, u);
            grandchild.Rebuild();
            Assert.True(ContainerView.Of(grandchild.Database).TryGetContainer(env, h, out var copy));
            Assert.Equal(new VecContainer(k), copy);

            parent.Union(k, m);
            parent.Rebuild();
            Assert.True(ContainerView.Of(parent.Database).TryGetContainer(env, h4, out var rebuilt));
            Assert.Equal(new VecContainer(k), rebuilt);
            Assert.False(grandchild.IsClean);
            var refused = Assert.Throws<NotSupportedException>(() => grandchild.Rebuild());
            Assert.Contains("does not yet merge", refused.Message);
            Assert.True(grandchild.IsFaulted);
            grandchild.Dispose();
            parent.CheckInvariants();
        }

        /// <summary>
        /// A writable parent claims, after its child did, a container the child claimed: both displace u, the
        /// element of the root's h, into k. The parent's container log records its copy [k] under h; the child's guard
        /// keeps that entry, since the child claimed h, and after the rebuild's passes finds it the only content under h
        /// in the parent's layer, which its own copy stands for. The child is not refused and keeps its copy.
        /// </summary>
        [Fact]
        public void AChildKeepsItsCopyOfAContainerItsWritableParentClaimsLater()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var k = root.NewId();
            var u = root.NewId();
            var h = Intern(root, new VecContainer(u));
            root.Freeze();

            var parent = root.CreateChild();
            var child = parent.CreateChild();
            child.Union(k, u);
            child.Rebuild();
            parent.Union(k, u);
            parent.Rebuild();
            Assert.True(parent.ContainerLayers!.LayerAt(env.TypeId)!.Shadowed.Contains(h));

            EqContainerGuard.Check(child);
            Assert.True(child.ContainerLayers!.HasEntriesOnClaimedIds);
            child.Rebuild();
            Assert.False(child.IsFaulted);
            Assert.True(child.IsClean);
            Assert.True(ContainerView.Of(child.Database).TryGetContainer(env, h, out var copy));
            Assert.Equal(new VecContainer(k), copy);
            child.CheckInvariants();
            parent.CheckInvariants();
        }

        /// <summary>
        /// A store with maps keyed by e-class ids registered at the root, their values not rebuilt, and the map
        /// {a: 1, b: 2, d: 4} interned there under x, over three classes a &lt; b &lt; d.
        /// </summary>
        static (Store Store, ContainerEnv<Relations.MapContainer> Env, uint A, uint B, uint D, uint X) MapSetup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var values = store.Root.Database.Containers;
            values.RegisterType<Relations.MapContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter), holdsIds: true);
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var d = root.NewId();
            var x = root.Database.WithExecutionState(state => state.Containers.GetOrInsert(new Relations.MapContainer(true, false, [a, b, d], [1, 2, 4]), state));
            return (store, values.GetEnvironment<Relations.MapContainer>(), a, b, d, x);
        }

        /// <summary>
        /// The root (not frozen) rebuilds in place a map keyed by classes that the child claimed, its keys collapsing in
        /// another order than the child's: the child's union of a and d makes its copy of x {a: 4, b: 2}, a map keeping
        /// the later of the keys that become one; then the root's union of a and b makes its x {a: 2, d: 4}. The root's
        /// map rebuilt against the child's union-find is {a: 4}, which the child's view does not hold, but the entry is
        /// the only content under x at the root, which the child's copy stands for: the child is not refused, and its
        /// copy, which imports the root's union, is {a: 2}, as a fresh root replaying the two unions, each with its
        /// rebuild, holds under x.
        /// </summary>
        [Fact]
        public void AClaimedMapTheRootRebuildsInPlaceIsHeldByTheChildsCopy()
        {
            var (store, env, a, b, d, x) = MapSetup();
            var root = store.Root;
            var child = root.CreateChild();
            child.Union(a, d);
            child.Rebuild();
            Assert.True(ContainerView.Of(child.Database).TryGetContainer(env, x, out var copy));
            Assert.Equal(new Relations.MapContainer(true, false, [a, b], [4, 2]), copy);

            root.Union(a, b);
            root.Rebuild();
            Assert.True(env.TryGetContainer(x, out var rebuilt));
            Assert.Equal(new Relations.MapContainer(true, false, [a, d], [2, 4]), rebuilt);

            EqContainerGuard.Check(child);
            Assert.True(child.ContainerLayers!.HasEntriesOnClaimedIds);
            child.Rebuild();
            Assert.False(child.IsFaulted);
            Assert.True(child.IsClean);
            Assert.True(ContainerView.Of(child.Database).TryGetContainer(env, x, out var held));
            Assert.Equal(new Relations.MapContainer(true, false, [a], [2]), held);
            child.CheckInvariants();
            child.Dispose();

            var (fresh, freshEnv, fa, fb, fd, fx) = MapSetup();
            fresh.Root.Union(fa, fd);
            fresh.Root.Rebuild();
            fresh.Root.Union(fa, fb);
            fresh.Root.Rebuild();
            Assert.True(freshEnv.TryGetContainer(fx, out var replayed));
            Assert.Equal(new Relations.MapContainer(true, false, [fa], [2]), replayed);
        }

        /// <summary>
        /// <see cref="AClaimedMapTheRootRebuildsInPlaceIsHeldByTheChildsCopy"/> under a frozen root and a writable
        /// parent, a case split's shape: the grandchild's union of a and d makes its copy of x {a: 4, b: 2}, then its
        /// writable parent's union of a and b claims x and rebuilds its copy to {a: 2, d: 4}, which the parent's
        /// container log records. That entry is the only content under x in the parent's layer, which the grandchild's
        /// copy stands for: the grandchild is not refused, and its copy becomes {a: 2}.
        /// </summary>
        [Fact]
        public void AGrandchildKeepsItsCopyOfAMapItsWritableParentRebuildsLater()
        {
            var (store, env, a, b, d, x) = MapSetup();
            var root = store.Root;
            root.Freeze();

            var parent = root.CreateChild();
            var grandchild = parent.CreateChild();
            grandchild.Union(a, d);
            grandchild.Rebuild();
            Assert.True(ContainerView.Of(grandchild.Database).TryGetContainer(env, x, out var copy));
            Assert.Equal(new Relations.MapContainer(true, false, [a, b], [4, 2]), copy);

            parent.Union(a, b);
            parent.Rebuild();
            Assert.True(ContainerView.Of(parent.Database).TryGetContainer(env, x, out var rebuilt));
            Assert.Equal(new Relations.MapContainer(true, false, [a, d], [2, 4]), rebuilt);

            Assert.False(grandchild.IsClean);
            grandchild.Rebuild();
            Assert.False(grandchild.IsFaulted);
            Assert.True(grandchild.IsClean);
            Assert.True(ContainerView.Of(grandchild.Database).TryGetContainer(env, x, out var held));
            Assert.Equal(new Relations.MapContainer(true, false, [a], [2]), held);
            grandchild.CheckInvariants();
            parent.CheckInvariants();
            grandchild.Dispose();
            parent.Dispose();
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
        /// An imported displacement of a held id needs no claim: the root rebuilt its container, and the child reads the
        /// rebuilt one. The guard moves past the root's new containers unread, since the child has neither a
        /// displacement nor a container layer when it runs.
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
        /// A child's database refuses egglog's container rebuild and egglog's pass: a child rebuilds containers only
        /// through its context's rebuild, in its container layers.
        /// </summary>
        [Fact]
        public void AChildsDatabaseRefusesTheContainerRebuild()
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
        /// A child may not hold a view in which an eq-sort container names one of its own losers. A container the
        /// (writable) root interns after the child's rebuild, naming the child's loser, is checked at the child's next
        /// rebuild, which refuses the child (<see cref="NotSupportedException"/>, faulted) unless the two containers
        /// are equal in it: in a fresh root replaying the chain, the containers [x] and [y] become equal once x and y
        /// are, and their ids are unioned.
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
