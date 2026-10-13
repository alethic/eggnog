using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for containers under colored contexts, storage part (colored-contexts spec §7.4, §10.7, X31, decision 9; C13
    /// interim): the dirty log through which children re-stamp their own rows naming a container the root rebuilt in
    /// place, cleared when the root's last descendant goes; the eq guard (G-b); the refusal to intern an eq-sort container
    /// in a child (G-a); and children never rebuilding containers.
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
            context.Database.WithExecutionState(state => state.Containers.RegisterValue(container, state));

        /// <summary>
        /// The root rebuilds a container in place while it has a live child: the container's id goes to the dirty log,
        /// and the child's rebuild re-stamps its own row naming it (§7.4); the log is cleared with the last descendant.
        /// </summary>
        [Fact]
        public void AChildRestampsItsRowsNamingAContainerTheRootRebuiltInPlace()
        {
            var (store, f) = Setup();
            var root = store.Root;
            var k = root.NewId();
            var w = root.NewId();
            var v = Intern(root, new VecContainer(w));
            var child = root.CreateChild();
            Insert(child, f, v, 5, 0);
            var stamp = Row(child, f, v)![2];
            Assert.Equal(0, store.ContainerDirtyLog.End);

            // The root merges w into k: the vector [w] becomes [k] under the same id, rebuilt in place.
            root.Union(k, w);
            child.Rebuild();
            Assert.True(root.Database.Containers.TryGetValue<VecContainer>(v, out var rebuilt));
            Assert.Equal(new VecContainer(k), rebuilt);
            Assert.Equal(1, store.ContainerDirtyLog.End);

            var restamped = Row(child, f, v)!;
            Assert.Equal(5u, restamped[1]);
            Assert.True(restamped[2] > stamp, "the child's own row naming the dirty container was re-stamped");
            child.CheckInvariants();

            child.Dispose();
            Assert.Equal(0, store.ContainerDirtyLog.End);
        }

        /// <summary>
        /// Without a live descendant the root logs nothing: no child will read it.
        /// </summary>
        [Fact]
        public void ARootWithoutDescendantsLogsNothing()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var k = root.NewId();
            var w = root.NewId();
            Intern(root, new VecContainer(w));
            root.Union(k, w);
            root.Rebuild();
            Assert.Equal(0, store.ContainerDirtyLog.End);
        }

        /// <summary>
        /// G-b: a child that displaces an id an eq-sort container holds is refused (and faulted); one that displaces the
        /// other id of the union is not; an import of a parent's displacement is not checked (the root renamed it).
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
        /// G-b against containers that appear later: the root (not frozen) interns an eq-sort container naming an id the
        /// child displaced in an earlier rebuild. The child is not clean from then on, even though no row changed; its
        /// next rebuild checks its whole journal against the new container and refuses it (faulted), where a copy would
        /// have unioned the containers.
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
        /// A frozen context interns no new eq-sort container (§7.4): its descendants would never re-run their guard
        /// against it. Looking one up, and interning one that exists or one over base values, stay allowed.
        /// </summary>
        [Fact]
        public void AFrozenContextInternsNoNewEqContainer()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var w = root.NewId();
            var v = Intern(root, new VecContainer(w));
            root.Freeze();
            Assert.Equal(v, Intern(root, new VecContainer(w)));
            var version = root.Database.Containers.EqVersion;
            Assert.Throws<InvalidOperationException>(() => Intern(root, new VecContainer(w, w)));
            Assert.Equal(version, root.Database.Containers.EqVersion);
            Assert.False(root.Database.Containers.GetEnvironment<VecContainer>().TryGetId(new VecContainer(w, w), out _));

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
        /// G-a: a child may look an eq-sort container up but not intern one; containers over base values it may intern,
        /// on the shared counter, so their ids are global.
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
        /// Children never rebuild containers (§7.4): a child's database refuses the container rebuild and egglog's pass.
        /// </summary>
        [Fact]
        public void ChildrenNeverRebuildContainers()
        {
            var (store, f) = Setup();
            var child = store.Root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => child.Database.RebuildContainers(store.UnionFindTable));
            Assert.Throws<InvalidOperationException>(() => child.Database.RebuildPass(store.UnionFindTable, [f]));
        }

    }

}
