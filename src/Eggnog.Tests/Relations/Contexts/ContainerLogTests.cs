using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the per-level container logs (<see cref="ContainerLog"/>): a level's log is active exactly while it
    /// has live descendants and is cleared with the last one, at every level, also when a child fails to be created,
    /// and a disposed child's log keeps none of its environments; a frozen level is never activated, and a frozen
    /// ancestor's tail is read before it settles; a cursor past a log's end reads nothing; a grandchild reads the root's
    /// log through its own cursor, and a container layer makes a child unclean as a displacement does; interns racing at
    /// a parallel root and the renames of a parallel rebuild are each logged once; and containers of values are never
    /// logged, so they leave children clean.
    /// </summary>
    /// <remarks>
    /// egglog has no writable root under a live <c>(push)</c>, so where a child claims a container that a writable root
    /// interned after the child's union, the view expected is that of a fresh root replaying the same commands in the
    /// same order, which interns the vector canonical, as <c>[x]</c>, under the same id.
    /// </remarks>
    public class ContainerLogTests
    {

        /// <summary>
        /// A store with the vector type registered at the root, as holding e-class ids, and its root environment.
        /// </summary>
        static (Store Store, ContainerEnv<VecContainer> Env) Setup(ParallelHeuristics? parallelism = null)
        {
            var store = new Store(parallelism ?? ParallelHeuristics.Serial);
            var values = store.Root.Database.Containers;
            values.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
            return (store, values.GetEnvironment<VecContainer>());
        }

        /// <summary>
        /// Interns <paramref name="container"/> through <paramref name="context"/>'s database.
        /// </summary>
        static uint Intern<TContainer>(Context context, TContainer container)
            where TContainer : struct, IContainerValue<TContainer>
        {
            return context.Database.WithExecutionState(state => state.Containers.GetOrInsert(container, state));
        }

        /// <summary>
        /// Drains <paramref name="log"/> and returns every Added entry it holds.
        /// </summary>
        static List<ContainerLogEntry> Entries(ContainerLog log)
        {
            log.Drain();
            var entries = new List<ContainerLogEntry>();
            log.ReadAdded(0, entries);
            return entries;
        }

        /// <summary>
        /// Advances the store's clock so that only <paramref name="left"/> epochs remain.
        /// </summary>
        static void LeaveEpochs(Store store, long left)
        {
            store.Counters.IncrementBy(store.ClockCounter, Value.StaleRep - left - store.Counters.Read(store.ClockCounter));
        }

        /// <summary>
        /// Every level that is not frozen activates its log when a child is created below it, at any depth, and the
        /// child's cursors start at the logs' ends, after what each level recorded before; disposing clears and
        /// deactivates exactly the logs whose level has no live descendant left, entries included. A child's own
        /// inserts go to its own log.
        /// </summary>
        [Fact]
        public void ALogIsActiveWhileItsLevelHasLiveDescendantsAndClearedWithTheLast()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var x = Intern(root, new VecContainer(a));
            var rootLog = store.ContainerLog;
            Assert.False(rootLog.Active);
            Assert.False(rootLog.Pending);

            var middle = root.CreateChild();
            var middleLog = middle.ContainerLog;
            Assert.Same(middle.ContainerLayers!.Log, middleLog);
            Assert.True(rootLog.Active);
            Assert.False(middleLog.Active);
            Assert.Equal(0, rootLog.AddedCount);

            var first = middle.CreateChild();
            Assert.True(rootLog.Active);
            Assert.True(middleLog.Active);
            Assert.False(first.ContainerLog.Active);

            // The root interns, and the middle places a container in its own layer (a rename of x there): each is
            // recorded in its own level's log, buffered until a reader drains it.
            var y = Intern(root, new VecContainer(b));
            middle.ContainerLayers.Place(env, new VecContainer(a, b), x);
            Assert.True(rootLog.Pending);
            Assert.True(middleLog.Pending);

            // A second grandchild's cursors start after both entries: its creation drained the logs.
            var second = middle.CreateChild();
            Assert.False(rootLog.Pending);
            Assert.False(middleLog.Pending);
            Assert.Equal(new[] { new ContainerLogEntry(env.TypeId, y, new VecContainer(b).ComputeHash()) }, Entries(rootLog));
            Assert.Equal(new[] { new ContainerLogEntry(env.TypeId, x, new VecContainer(a, b).ComputeHash()) }, Entries(middleLog));
            Assert.Equal(1, second.Tables!.CursorAt(0)!.Added);
            Assert.Equal(1, second.Tables.CursorAt(1)!.Added);
            Assert.Equal(0, first.Tables!.CursorAt(0)!.Added);
            Assert.Equal(0, first.Tables.CursorAt(1)!.Added);
            second.CheckInvariants();

            // The middle keeps a live descendant until both grandchildren go; then its log is empty and inactive,
            // while the root's lives on with the middle.
            first.Dispose();
            Assert.True(middleLog.Active);
            Assert.Equal(1, middleLog.AddedCount);
            second.Dispose();
            Assert.False(middleLog.Active);
            Assert.Equal(0, middleLog.AddedCount);
            Assert.True(rootLog.Active);
            Assert.Equal(1, rootLog.AddedCount);
            middle.CheckInvariants();

            middle.Dispose();
            Assert.False(rootLog.Active);
            Assert.Equal(0, rootLog.AddedCount);
            Assert.False(rootLog.Pending);
            root.CheckInvariants();
        }

        /// <summary>
        /// A disposed child's log lets go of its layers' own environments, which its layers drop, so they can be
        /// collected while the child is still referenced.
        /// </summary>
        [Fact]
        public void ADisposedChildsLogLetsGoOfItsLayersEnvironments()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var x = Intern(root, new VecContainer(a));
            var child = root.CreateChild();
            var own = PlaceAndTrackOwn(child, env, new VecContainer(a, a), x);
            child.Dispose();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(own.IsAlive);
            GC.KeepAlive(child);
        }

        /// <summary>
        /// Places <paramref name="container"/> under <paramref name="id"/> in <paramref name="child"/>'s layer and
        /// returns a weak reference to the layer's own environment; apart, so that no local of the caller holds it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference PlaceAndTrackOwn(Context child, ContainerEnv<VecContainer> env, VecContainer container, uint id)
        {
            child.ContainerLayers!.Place(env, container, id);
            return new WeakReference(child.ContainerLayers.LayerOf(env)!.OwnBase);
        }

        /// <summary>
        /// A frozen level is never activated: a child of a frozen root leaves the root's log inactive and has no cursor
        /// into it, and a grandchild activates only its writable parent's log. A frozen child's log never grows again.
        /// </summary>
        [Fact]
        public void AFrozenLevelIsNeverActivated()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            Intern(root, new VecContainer(a));
            root.Freeze();

            var middle = root.CreateChild();
            Assert.False(store.ContainerLog.Active);
            Assert.Equal(1, middle.Tables!.SettledDepth);

            var leaf = middle.CreateChild();
            Assert.False(store.ContainerLog.Active);
            Assert.True(middle.ContainerLog.Active);
            Assert.Null(leaf.Tables!.CursorAt(0));
            Assert.NotNull(leaf.Tables.CursorAt(1));
            leaf.Dispose();
            Assert.False(middle.ContainerLog.Active);

            // Frozen with a live descendant, the middle keeps its log active, but nothing is recorded there any more.
            var late = middle.CreateChild();
            middle.Freeze();
            Assert.True(middle.ContainerLog.Active);
            Assert.Throws<InvalidOperationException>(() => middle.ContainerLayers!.Place(env, new VecContainer(a, a), middle.NewId()));
            Assert.Equal(0, middle.ContainerLog.AddedCount);
            middle.CheckInvariants();
            late.Dispose();
            Assert.False(middle.ContainerLog.Active);
        }

        /// <summary>
        /// Children created under a writable root that then freezes read what the root's log holds, the tail the freeze
        /// drained, before they settle the root and drop their cursors: a child whose losers the tail's containers do not
        /// name settles clean, and one whose loser a container of the tail names claims that container, which only the
        /// tail can tell it of, since its journal was handled before that container existed. The root's log stays
        /// active, and grows no more, until both children go.
        /// </summary>
        [Fact]
        public void AFrozenAncestorsTailIsReadBeforeItSettles()
        {
            var (store, env) = Setup();
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var z = root.NewId();
            var w = root.NewId();
            var child = root.CreateChild();
            var claiming = root.CreateChild();
            child.Union(x, y);
            child.Rebuild();
            claiming.Union(x, w);
            claiming.Rebuild();

            // The root interns three containers, the last naming the second child's loser, and writes a row, then
            // freezes, which drains its log.
            Intern(root, new VecContainer(z));
            Intern(root, new VecContainer(z, z));
            var held = Intern(root, new VecContainer(w));
            Insert(root, f, z, 1, 0);
            root.Freeze();
            var log = store.ContainerLog;
            Assert.False(log.Pending);
            Assert.Equal(3, log.AddedCount);
            Assert.False(child.IsClean);
            Assert.False(claiming.IsClean);

            child.Rebuild();
            Assert.True(child.IsClean);
            Assert.Equal(1, child.Tables!.SettledDepth);
            Assert.Null(child.Tables.CursorAt(0));
            Assert.True(log.Active);
            Assert.Equal(3, log.AddedCount);
            Assert.True(child.ContainerLayers!.IsEmpty);
            child.CheckInvariants();
            root.CheckInvariants();

            claiming.Rebuild();
            Assert.False(claiming.IsFaulted);
            Assert.True(claiming.IsClean);
            Assert.Equal(1, claiming.Tables!.SettledDepth);
            Assert.True(ContainerView.Of(claiming.Database).TryGetContainer(env, held, out var copy));
            Assert.Equal(new VecContainer(x), copy);
            claiming.CheckInvariants();
            claiming.Dispose();
            Assert.True(log.Active);
            Assert.Equal(3, log.AddedCount);

            child.Dispose();
            Assert.False(log.Active);
            Assert.Equal(0, log.AddedCount);
        }

        /// <summary>
        /// A child whose construction throws, here because the epochs ran out, leaves no log active that no gate counts
        /// a descendant for: a level with a live descendant keeps its log active, the creating child's own log, which
        /// the attempt activated, is cleared, and so is the log of a root left without descendants.
        /// </summary>
        [Fact]
        public void AChildThatFailsToBeCreatedLeavesNoLogActive()
        {
            var (store, _) = Setup();
            var root = store.Root;
            var middle = root.CreateChild();
            var rootLog = store.ContainerLog;
            Assert.True(rootLog.Active);
            LeaveEpochs(store, 0);

            Assert.Throws<OverflowException>(() => middle.CreateChild());
            Assert.False(middle.ContainerLog.Active);
            Assert.True(rootLog.Active);
            middle.CheckInvariants();
            Assert.Throws<OverflowException>(() => root.CreateChild());
            Assert.True(rootLog.Active);

            middle.Dispose();
            Assert.False(rootLog.Active);
            Assert.Throws<OverflowException>(() => root.CreateChild());
            Assert.False(rootLog.Active);
            Assert.False(rootLog.Pending);
            root.CheckInvariants();
        }

        /// <summary>
        /// A cursor past the end of a log, which only a clear could leave, reads nothing and comes back at the end, in
        /// both streams; a child whose Added cursor is past the end is clean, and its pre-loop scan moves the cursor
        /// back to the end, reading what comes later.
        /// </summary>
        [Fact]
        public void ACursorPastTheEndReadsNothing()
        {
            var log = new ContainerLog();
            log.Activate();
            log.AppendDirty([1, 2, 3]);
            Assert.Empty(log.ReadDirty(5, out var end));
            Assert.Equal(3, end);
            Assert.Equal(new uint[] { 2, 3 }, log.ReadDirty(1, out end));
            Assert.Equal(3, end);
            var entries = new List<ContainerLogEntry>();
            Assert.Equal(0, log.ReadAdded(7, entries));
            Assert.Empty(entries);
            Assert.False(log.HasAddedSince(7));
            log.Clear();
            Assert.Equal(0, log.DirtyCount);
            Assert.Empty(log.ReadDirty(3, out end));
            Assert.Equal(0, end);

            var (store, env) = Setup();
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var z = root.NewId();
            var child = root.CreateChild();
            child.Union(x, y);
            child.Rebuild();
            var cursor = child.Tables!.CursorAt(0)!;
            cursor.Added = 5;
            Assert.True(child.IsClean);

            // A container the root interns now lies before the cursor: pending, so the child rebuilds, reads nothing
            // and comes back at the end. The next one, naming the child's loser, lies past it and is claimed.
            Intern(root, new VecContainer(z));
            Assert.False(child.IsClean);
            Assert.False(child.Rebuild());
            Assert.Equal(1, cursor.Added);
            Assert.True(child.IsClean);
            var u = Intern(root, new VecContainer(y));
            Assert.False(child.IsClean);
            Assert.True(child.Rebuild());
            Assert.Equal(2, cursor.Added);
            Assert.False(child.IsFaulted);
            Assert.True(ContainerView.Of(child.Database).TryGetContainer(env, u, out var copy));
            Assert.Equal(new VecContainer(x), copy);
            child.CheckInvariants();
            child.Dispose();
        }

        /// <summary>
        /// A grandchild reads the root's log through its own cursor into the root: once its parent, with no
        /// displacement, has consumed a new container unread, the grandchild whose loser the container names is still
        /// unclean and claims the container, while its parent stays clean, holding no layer.
        /// </summary>
        [Fact]
        public void AGrandchildReadsTheRootsLogThroughItsOwnCursor()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var middle = root.CreateChild();
            var leaf = middle.CreateChild();
            leaf.Union(x, y);
            leaf.Rebuild();
            Assert.True(leaf.IsClean);

            var u = Intern(root, new VecContainer(y));
            Assert.False(middle.Rebuild());
            Assert.Equal(1, middle.Tables!.CursorAt(0)!.Added);
            Assert.Equal(0, leaf.Tables!.CursorAt(0)!.Added);
            Assert.True(middle.IsClean);
            Assert.False(leaf.IsClean);

            Assert.True(leaf.Rebuild());
            Assert.False(leaf.IsFaulted);
            Assert.Equal(1, leaf.Tables.CursorAt(0)!.Added);
            Assert.True(ContainerView.Of(leaf.Database).TryGetContainer(env, u, out var copy));
            Assert.Equal(new VecContainer(x), copy);
            Assert.True(ContainerView.Of(middle.Database).TryGetContainer(env, u, out var original));
            Assert.Equal(new VecContainer(y), original);
            Assert.True(middle.IsClean);
            Assert.True(middle.ContainerLayers!.IsEmpty);
            leaf.CheckInvariants();
            leaf.Dispose();
            middle.CheckInvariants();
        }

        /// <summary>
        /// A child with no displacement but a container layer is not clean while the root's log holds a container of
        /// classes it has not read, as a child with a displacement is not; a rebuild that only reads the log makes it
        /// clean again, running no pass and taking no epoch.
        /// </summary>
        [Fact]
        public void AChildWithAContainerLayerIsUncleanUntilItReadsTheRootsLog()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var x = Intern(root, new VecContainer(a));
            var child = root.CreateChild();
            child.ContainerLayers!.Place(env, new VecContainer(a, b), x);
            Assert.Equal(0, child.Journal!.Count);
            Assert.False(child.ContainerLayers.IsEmpty);
            Assert.True(child.IsClean);

            Intern(root, new VecContainer(b));
            Assert.False(child.IsClean);
            var clock = store.ReadClock();
            Assert.False(child.Rebuild());
            Assert.Equal(clock, store.ReadClock());
            Assert.True(child.IsClean);
            Assert.Equal(1, child.Tables!.CursorAt(0)!.Added);
            child.CheckInvariants();
        }

        /// <summary>
        /// Interns racing at a root with many hash-cons shards, while it has a live child, are each logged once, under
        /// the id every racer got, whatever shard they buffer in; the child, with no displacement, stays clean.
        /// </summary>
        [Fact]
        public void ParallelRootInternsAreLoggedOnceEach()
        {
            const int Contents = 3000;
            const int Racers = 8;
            var (store, env) = Setup(new ParallelHeuristics(4));
            var root = store.Root;
            var child = root.CreateChild();
            var start = new Notification();
            var ids = new uint[Racers, Contents];
            var threads = ConcurrencyTestThread.StartMany(Racers, racer =>
            {
                start.Wait();
                for (var i = 0; i < Contents; i++)
                {
                    // Each racer walks the contents from its own offset, so racers collide on every content.
                    var c = (i + racer * (Contents / Racers)) % Contents;
                    ids[racer, c] = env.GetOrInsertAtRoot(new VecContainer((uint)c, 7), store.Counters);
                }
            });
            start.Notify();
            ConcurrencyTestThread.JoinAll(threads);

            var log = store.ContainerLog;
            Assert.True(log.Pending);
            var entries = Entries(log);
            Assert.Equal(Contents, entries.Count);
            Assert.Equal(Contents, entries.Distinct().Count());
            var expected = Enumerable.Range(0, Contents)
                .Select(c => new ContainerLogEntry(env.TypeId, ids[0, c], new VecContainer((uint)c, 7).ComputeHash()))
                .OrderBy(entry => entry)
                .ToList();
            Assert.Equal(expected, entries.OrderBy(entry => entry).ToList());
            for (var racer = 1; racer < Racers; racer++)
                for (var c = 0; c < Contents; c++)
                    Assert.Equal(ids[0, c], ids[racer, c]);

            Assert.True(child.IsClean);
            root.CheckInvariants();
            child.Dispose();
        }

        /// <summary>
        /// A full rebuild of a root environment large enough to run in parallel logs each container it renames in
        /// place under its new id and each it reinserts with new contents, once, and the latter's ids, rebuilt in place,
        /// as Dirty; nothing it leaves alone.
        /// </summary>
        [Fact]
        public void RenamesDuringAParallelRebuildAreLogged()
        {
            const int Containers = 1500;
            const int Renamed = 12;
            const int Rebuilt = 12;
            var parallelism = new ParallelHeuristics(4);
            var (store, env) = Setup(parallelism);
            var root = store.Root;

            // Ids minted first are smaller: each renamed container's id loses to one of them, and each rebuilt
            // container's element to another.
            var targets = Enumerable.Range(0, Renamed).Select(_ => root.NewId()).ToArray();
            var replacements = Enumerable.Range(0, Rebuilt).Select(_ => root.NewId()).ToArray();
            var elements = Enumerable.Range(0, Containers).Select(_ => root.NewId()).ToArray();
            var ids = elements.Select(e => Intern(root, new VecContainer(e))).ToArray();
            var child = root.CreateChild();
            var log = store.ContainerLog;
            Assert.Equal(0, log.AddedCount);
            Assert.False(log.Pending);

            var expected = new List<ContainerLogEntry>();
            for (var i = 0; i < Renamed; i++)
            {
                root.Union(targets[i], ids[i]);
                expected.Add(new ContainerLogEntry(env.TypeId, targets[i], new VecContainer(elements[i]).ComputeHash()));
            }

            var dirty = new List<uint>();
            for (var i = 0; i < Rebuilt; i++)
            {
                var j = Containers - 1 - i;
                root.Union(replacements[i], elements[j]);
                expected.Add(new ContainerLogEntry(env.TypeId, ids[j], new VecContainer(replacements[i]).ComputeHash()));
                dirty.Add(ids[j]);
            }

            // The full rebuild runs in parallel: too many displaced ids for the incremental one.
            Assert.True(parallelism.ParallelizeIntraContainerOp(env.Count));
            Assert.False(ContainerEnv<VecContainer>.IsIncremental(Renamed + Rebuilt, env.Count, true));
            root.Rebuild();

            var entries = Entries(log);
            Assert.Equal(expected.OrderBy(entry => entry).ToList(), entries.OrderBy(entry => entry).ToList());
            Assert.Equal(dirty.Order().ToArray(), log.ReadDirty(0, out _).Order().ToArray());
            for (var i = 0; i < Renamed; i++)
                Assert.True(env.TryGetContainer(targets[i], out _));

            env.CheckInvariants();
            root.CheckInvariants();
            child.Rebuild();
            child.CheckInvariants();
            child.Dispose();
            Assert.Equal(0, log.AddedCount);
        }

        /// <summary>
        /// Containers of values, in an environment that also holds containers of classes and in one of values only,
        /// are never logged, from the root, from a child or without a state: a child with a displacement stays clean.
        /// A container of classes the root interns then makes it unclean.
        /// </summary>
        [Fact]
        public void ContainersOfValuesLeaveChildrenClean()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var values = store.Root.Database.Containers;
            var merge = new UnionContainerMerge(store.UnionFindTable, store.ClockCounter);
            values.RegisterType<TaggedContainer<ContainerLogTests>>(store.IdCounter, merge, holdsIds: true);
            values.RegisterType<TaggedContainer<ContainerLog>>(store.IdCounter, merge, holdsIds: false);
            var mixed = values.GetEnvironment<TaggedContainer<ContainerLogTests>>();
            var plain = values.GetEnvironment<TaggedContainer<ContainerLog>>();
            Assert.Same(store.ContainerLog, mixed.Log);
            Assert.Null(plain.Log);

            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var child = root.CreateChild();
            child.Union(x, y);
            child.Rebuild();
            Assert.True(child.IsClean);

            // Base values, not ids: the test container visits its value as an element either way, and the eq-container
            // checks read the element index of an environment that holds ids. The environment of values is not read.
            Intern(root, new TaggedContainer<ContainerLogTests>(1001));
            Intern(child, new TaggedContainer<ContainerLogTests>(1002));
            mixed.GetOrInsertShared(new TaggedContainer<ContainerLogTests>(1003), store.Counters);
            Intern(root, new TaggedContainer<ContainerLog>(1004));
            Intern(child, new TaggedContainer<ContainerLog>(1005));
            plain.GetOrInsertShared(new TaggedContainer<ContainerLog>(y), store.Counters);
            var log = store.ContainerLog;
            Assert.True(log.Active);
            Assert.False(log.Pending);
            Assert.Equal(0, log.AddedCount);
            Assert.True(child.IsClean);
            Assert.Equal(x, child.Find(y));

            Intern(root, new TaggedContainer<ContainerLogTests>(x, holdsIds: true));
            Assert.True(log.Pending);
            Assert.False(child.IsClean);
            Assert.False(child.Rebuild());
            Assert.True(child.IsClean);
            child.CheckInvariants();
        }

    }

}
