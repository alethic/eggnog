using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for containers under colored contexts, storage part: the Dirty stream of the root's container log, through
    /// which children re-stamp their own rows naming a container the root rebuilt in place, cleared when the root's
    /// last descendant goes; a child's rebuild of the containers its own unions displace an element of, in its
    /// container layers; its catch-up with the containers its writable root or parent gains later, which it claims
    /// when they hold its losers, merges with its own copies when they are equal, and adopts when they land on an id
    /// it claimed, while its copy stands for a container it claimed that the ancestor rebuilds in place, a map whose
    /// keys collapse in another order included, each compared with a fresh root replaying the same commands; a child
    /// interning an eq-sort container in its own layers; and a child's database refusing egglog's container rebuild.
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
        /// A container that appears afterwards: the root (not frozen) interns an eq-sort container naming an id the
        /// child displaced in an earlier rebuild. The child is not clean from then on, even though no row changed; its
        /// next rebuild's pre-loop scan keeps the container, whose element is not canonical in the child, and the
        /// catch-up of its first pass claims it, so that its container pass rebuilds the copy: the child sees
        /// <c>[x]</c> under the root's new id, and the root keeps <c>[y]</c>. A fresh root replaying the union and then
        /// the intern holds <c>[x]</c> under that id, since a frontend interns contents canonical: egglog's binary at
        /// the engine's pin evaluates <c>(vec-of (Y))</c> to <c>(vec-of (X))</c> after <c>(union (X) (Y))</c> (probe
        /// <c>intern-after-union.egg</c>).
        /// </summary>
        [Fact]
        public void AContainerInternedAfterTheDisplacementIsClaimed()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var x = root.NewId();
            var y = root.NewId();
            var child = root.CreateChild();
            child.Union(x, y);
            child.Rebuild();
            Assert.True(child.IsClean);

            var u = Intern(root, new VecContainer(y));
            Assert.False(child.IsClean);
            Assert.Throws<InvalidOperationException>(() => child.Find(x));
            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.True(child.IsClean);
            var view = ContainerView.Of(child.Database);
            Assert.True(view.TryGetContainer(env, u, out var copy));
            Assert.Equal(new VecContainer(x), copy);
            Assert.True(view.TryGetId(env, new VecContainer(x), out var id));
            Assert.Equal(u, id);
            Assert.False(view.TryGetId(env, new VecContainer(y), out _));
            Assert.True(child.ContainerLayers!.LayerAt(env.TypeId)!.Shadowed.Contains(u));
            child.CheckInvariants();
            Assert.True(env.TryGetContainer(u, out var original));
            Assert.Equal(new VecContainer(y), original);
            child.Dispose();
        }

        /// <summary>
        /// A container the root's own container rebuild produces is new content as well: the root unions z into y, its
        /// vector <c>[z]</c> becomes <c>[y]</c> in place, and y is a loser of the child, so the child's catch-up claims
        /// it and its copy becomes <c>[x]</c>, as a fresh root replaying the two unions holds; the root keeps
        /// <c>[y]</c>.
        /// </summary>
        [Fact]
        public void AContainerTheRootsRebuildProducesIsClaimedToo()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var x = root.NewId();
            var y = root.NewId();
            var z = root.NewId();
            var v = Intern(root, new VecContainer(z));
            var child = root.CreateChild();
            child.Union(x, y);
            child.Rebuild();

            root.Union(y, z);
            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.Equal(x, child.Find(z));
            var view = ContainerView.Of(child.Database);
            Assert.True(view.TryGetContainer(env, v, out var copy));
            Assert.Equal(new VecContainer(x), copy);
            Assert.True(view.TryGetId(env, new VecContainer(x), out var id));
            Assert.Equal(v, id);
            Assert.False(view.TryGetId(env, new VecContainer(y), out _));
            child.CheckInvariants();
            Assert.True(env.TryGetContainer(v, out var rebuilt));
            Assert.Equal(new VecContainer(y), rebuilt);
        }

        /// <summary>
        /// A new container that names none of a child's losers costs it one pre-loop scan: the child is not clean
        /// until it rebuilds, and that rebuild consumes the container, runs no pass and takes no epoch
        /// (<see cref="ContainerLayers.ScanAdds"/>). A child with no displacement is not even made unclean.
        /// </summary>
        [Fact]
        public void AContainerNamingNoLoserOnlyCostsOneScan()
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
        /// the child's pre-loop scan keeps it, its element not canonical in the child, and the child's pass claims the
        /// new container and rebuilds it, as it would one the root held before, since the displacement is in the pass's
        /// delta.
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
            Assert.True(child.Tables!.HasNewAddedEntries);
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
        /// A collision: the root (not frozen) interns, after the child's container pass rebuilt a claimed copy, a
        /// container equal to the copy under another id: the child's view would hold [k] twice, under the claimed v
        /// and under the root's new u. The child's catch-up settles it at once: the smaller v wins, the child claims u,
        /// whose placement merges into v, and u is merged into v. A fresh root replaying the union and then the intern
        /// returns v for [k], and never mints u; the child holds that, with u merged away.
        /// </summary>
        [Fact]
        public void AContainerTheRootInternsEqualToAClaimedCopyMergesWithIt()
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
            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.Equal(v, child.Find(u));
            var view = ContainerView.Of(child.Database);
            Assert.True(view.TryGetId(env, new VecContainer(k), out var id));
            Assert.Equal(v, id);
            Assert.True(view.TryGetContainer(env, v, out var held));
            Assert.Equal(new VecContainer(k), held);
            Assert.False(view.TryGetContainer(env, u, out _));
            child.CheckInvariants();
            child.Dispose();
        }

        /// <summary>
        /// The root (not frozen) puts content on an id the child claimed: it unions the ids of the vectors [w] and [a],
        /// so its rebuild renames [a] onto the smaller, y, which the child claimed when its union displaced w, and
        /// whose copy it holds as [k]. The child's shadow hides the root's [a] under y; its catch-up finds that z, a
        /// displacement it imports, is y in the parent, so content arrived under y, and adopts it: the child's layer
        /// holds [k] and [a] under y, and z is merged into y, as a fresh root replaying the chain holds them
        /// (<see cref="TheFreshRootCopyOfTheRenameHoldsBothContents"/>).
        /// </summary>
        [Fact]
        public void AContainerTheRootRenamesOntoAClaimedIdIsAdopted()
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

            // The pre-loop scan keeps the root's entry under the claimed y for the catch-up.
            child.ContainerLayers.ScanAdds();
            Assert.True(child.ContainerLayers.HasPending);
            Assert.False(child.IsClean);
            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.False(child.ContainerLayers.HasPending);
            Assert.Equal(y, child.Find(z));
            var view = ContainerView.Of(child.Database);
            Assert.True(view.TryGetId(env, new VecContainer(k), out var ofK));
            Assert.Equal(y, ofK);
            Assert.True(view.TryGetId(env, new VecContainer(a), out var ofA));
            Assert.Equal(y, ofA);
            Assert.False(view.TryGetId(env, new VecContainer(w), out _));
            var own = ((ContainerLayer<VecContainer>)child.ContainerLayers.LayerAt(env.TypeId)!).Own;
            Assert.Equal(2, own.CollectById(y, new List<ContainerEntry<VecContainer>>(), locked: true));
            child.CheckInvariants();
            child.Dispose();
        }

        /// <summary>
        /// The copy <see cref="AContainerTheRootRenamesOntoAClaimedIdIsAdopted"/> compares with: the same chain replayed
        /// in a fresh root holds [k] and [a] under one id, y.
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
        /// child displaced into k, makes its [w] under y [a], which its container log records under y. The child's
        /// pre-loop scan keeps that entry, since the child claimed y, and its catch-up finds that no displacement it
        /// imports is y in the parent: the entry is the content the child copied, rebuilt in place, which the child's
        /// copy stands for. The child imports the root's union, under which a is k there, and keeps its copy [k], the
        /// entry's content rebuilt, alone under y, as a fresh root replaying the two unions holds it.
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

            child.ContainerLayers.ScanAdds();
            Assert.True(child.ContainerLayers.HasPending);
            child.Rebuild();
            Assert.False(child.IsFaulted);
            Assert.False(child.ContainerLayers.HasPending);
            Assert.True(child.IsClean);
            Assert.Equal(k, child.Find(a));
            var view = ContainerView.Of(child.Database);
            Assert.True(view.TryGetContainer(env, y, out var copy));
            Assert.Equal(new VecContainer(k), copy);
            Assert.True(view.TryGetId(env, new VecContainer(k), out var id));
            Assert.Equal(y, id);
            var own = ((ContainerLayer<VecContainer>)child.ContainerLayers.LayerAt(env.TypeId)!).Own;
            Assert.Equal(1, own.CollectById(y, new List<ContainerEntry<VecContainer>>(), locked: true));
            child.CheckInvariants();
        }

        /// <summary>
        /// The catch-up reads a writable parent's container log as it reads the root's: under a frozen root, a
        /// grandchild claims h, the root's [u], when it displaces u into k, and holds the copy [k]; then its writable
        /// parent's union of k and m claims h4, the root's [m], and rebuilds it to [k], which the parent's container
        /// log records. The grandchild's view would hold [k] under h and under h4, a collision its catch-up settles:
        /// the smaller h wins, the grandchild claims h4 from its parent, whose placement merges into h, and h4 is
        /// merged into h, as a fresh root replaying the grandchild's union and then the parent's holds [k] under h.
        /// </summary>
        [Fact]
        public void AContainerAWritableParentRebuildsEqualToAGrandchildsCopyMergesWithIt()
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
            Assert.True(grandchild.Rebuild());
            Assert.False(grandchild.IsFaulted);
            Assert.Equal(h, grandchild.Find(h4));
            var view = ContainerView.Of(grandchild.Database);
            Assert.True(view.TryGetId(env, new VecContainer(k), out var id));
            Assert.Equal(h, id);
            Assert.True(view.TryGetContainer(env, h, out var held));
            Assert.Equal(new VecContainer(k), held);
            Assert.False(view.TryGetContainer(env, h4, out _));
            Assert.True(grandchild.ContainerLayers!.LayerAt(env.TypeId)!.Shadowed.Contains(h4));
            grandchild.CheckInvariants();
            grandchild.Dispose();
            parent.CheckInvariants();
        }

        /// <summary>
        /// A writable parent claims, after its child did, a container the child claimed: both displace u, the
        /// element of the root's h, into k. The parent's container log records its copy under h; the child's
        /// pre-loop scan keeps those entries, since the child claimed h, and its catch-up finds that no displacement it
        /// imports is h in the parent: the parent's entry is its own copy of the content the child copied, which the
        /// child's copy stands for. The child keeps its copy, and holds nothing else under h.
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

            child.ContainerLayers!.ScanAdds();
            Assert.True(child.ContainerLayers.HasPending);
            child.Rebuild();
            Assert.False(child.IsFaulted);
            Assert.True(child.IsClean);
            Assert.True(ContainerView.Of(child.Database).TryGetContainer(env, h, out var copy));
            Assert.Equal(new VecContainer(k), copy);
            var own = ((ContainerLayer<VecContainer>)child.ContainerLayers.LayerAt(env.TypeId)!).Own;
            Assert.Equal(1, own.CollectById(h, new List<ContainerEntry<VecContainer>>(), locked: true));
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
        /// map rebuilt against the child's union-find is {a: 4}, which the child's view does not hold, but no
        /// displacement the child imports is x in the parent: the entry is the content the child copied, rebuilt in
        /// place, which the child's copy stands for, so the child adopts nothing. Its copy, which imports the root's
        /// union, is {a: 2}, alone under x, as a fresh root replaying the two unions, each with its rebuild, holds
        /// under x.
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

            child.ContainerLayers!.ScanAdds();
            Assert.True(child.ContainerLayers.HasPending);
            child.Rebuild();
            Assert.False(child.IsFaulted);
            Assert.True(child.IsClean);
            Assert.True(ContainerView.Of(child.Database).TryGetContainer(env, x, out var held));
            Assert.Equal(new Relations.MapContainer(true, false, [a], [2]), held);
            var own = ((ContainerLayer<Relations.MapContainer>)child.ContainerLayers.LayerAt(env.TypeId)!).Own;
            Assert.Equal(1, own.CollectById(x, new List<ContainerEntry<Relations.MapContainer>>(), locked: true));
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
        /// container log records. No displacement the grandchild imports is x in the parent: the parent's entry is its
        /// own copy of the content the grandchild copied, which the grandchild's copy stands for, so it adopts nothing,
        /// and its copy becomes {a: 2}, alone under x.
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
            var own = ((ContainerLayer<Relations.MapContainer>)grandchild.ContainerLayers!.LayerAt(env.TypeId)!).Own;
            Assert.Equal(1, own.CollectById(x, new List<ContainerEntry<Relations.MapContainer>>(), locked: true));
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
            var refused = Assert.Throws<InvalidOperationException>(() => Intern(root, new VecContainer(w, w)));
            Assert.Contains("cannot be interned in a frozen context", refused.Message, StringComparison.Ordinal);
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
        /// rebuilt one. The pre-loop scan moves past the root's new containers unread, since the child has neither a
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
            Assert.False(child.ContainerLayers!.HasPending);
            Assert.True(child.ContainerLayers.IsEmpty);
        }

        /// <summary>
        /// A child interns an eq-sort container its view does not hold in its own container layer, under an id local to
        /// it that no ancestor or sibling sees, and one its view holds it finds there, as egglog's push interns in its
        /// copy; a frozen child interns none. Containers over base values it interns in the root's environment, on the
        /// shared counter, so their ids are global.
        /// </summary>
        [Fact]
        public void AChildInternsEqContainersInItsLayersAndBaseValueContainersGlobally()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var w = root.NewId();
            var v = Intern(root, new VecContainer(w));
            var child = root.CreateChild();
            Assert.Equal(v, Intern(child, new VecContainer(w)));
            Assert.True(child.ContainerLayers!.IsEmpty);

            var ww = Intern(child, new VecContainer(w, w));
            Assert.True(child.IsLocalId(ww));
            Assert.Equal(ww, Intern(child, new VecContainer(w, w)));
            Assert.Equal(1, child.ContainerLayers.LayerAt(env.TypeId)!.OwnBase.Count);
            Assert.True(ContainerView.Of(child.Database).TryGetContainer(env, ww, out var made));
            Assert.Equal(new VecContainer(w, w), made);
            Assert.False(env.TryGetId(new VecContainer(w, w), out _));
            Assert.Equal(1, env.Count);

            var sibling = root.CreateChild();
            Assert.False(ContainerView.Of(sibling.Database).TryGetId(env, new VecContainer(w, w), out _));
            child.CheckInvariants();

            // A frozen child finds what it holds, and makes nothing.
            sibling.Dispose();
            root.Freeze();
            child.Freeze();
            Assert.Equal(ww, Intern(child, new VecContainer(w, w)));
            var refused = Assert.Throws<InvalidOperationException>(() => Intern(child, new VecContainer(w, w, w)));
            Assert.Contains("cannot be interned in a frozen context", refused.Message, StringComparison.Ordinal);
            Assert.Equal(1, child.ContainerLayers.LayerAt(env.TypeId)!.OwnBase.Count);

            var (baseStore, _) = Setup(holdsIds: false);
            var baseChild = baseStore.Root.CreateChild();
            var id = Intern(baseChild, new VecContainer(false, 1, 2));
            Assert.False(baseChild.IsLocalId(id));
            Assert.Equal(id, Intern(baseStore.Root, new VecContainer(false, 1, 2)));
            Assert.True(baseChild.ContainerLayers!.IsEmpty);
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
        /// A child may not hold a view in which an eq-sort container names one of its own losers. The (writable) root
        /// interns, after the child's rebuild, [x] and [y], naming the child's loser y, and writes rows naming them: the
        /// child's catch-up claims [y], whose copy rebuilds to [x] and meets the root's [x], so the smaller id wins and
        /// the two container ids are one in the child, with their rows met, as in a fresh root replaying the chain
        /// (<see cref="TheFreshRootCopyOfThatChainEquatesTheContainers"/>).
        /// </summary>
        [Fact]
        public void AnEqContainerInternedByTheRootAfterAChildDisplacedItsElementIsMerged()
        {
            var (store, f) = ContainerSetup();
            var root = store.Root;
            var env = root.Database.Containers.GetEnvironment<VecContainer>();
            var x = root.NewId();
            var y = root.NewId();
            var child = root.CreateChild();

            // y loses to x in the child; no container holds y yet.
            child.Union(x, y);
            child.Rebuild();
            Assert.Equal(x, child.Find(y));

            // The root (not frozen) interns [x] and [y] and names them in rows the child will read.
            var kx = Intern(root, new VecContainer(x));
            var ky = Intern(root, new VecContainer(y));
            Insert(root, f, kx, 1, 0);
            Insert(root, f, ky, 2, 0);

            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.True(child.AreEqual(kx, ky));
            Assert.Equal(kx, child.Find(ky));
            Assert.True(ContainerView.Of(child.Database).TryGetId(env, new VecContainer(x), out var id));
            Assert.Equal(kx, id);
            Assert.False(ContainerView.Of(child.Database).TryGetContainer(env, ky, out _));
            Assert.Equal(2u, Row(child, f, kx)![1]);
            Assert.Null(Row(child, f, ky));
            child.CheckInvariants();
            Assert.False(root.AreEqual(kx, ky));
        }

        /// <summary>
        /// The copy <see cref="AnEqContainerInternedByTheRootAfterAChildDisplacedItsElementIsMerged"/> compares with: the
        /// same chain replayed in a fresh root unions the two container ids and meets their rows.
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
            Assert.Equal(kx, root.Find(ky));
            Assert.Equal(2u, Row(root, f, kx)![1]);
        }

    }

}
