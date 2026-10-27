using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child's container layers and the view that reads them (<see cref="ContainerLayers"/>,
    /// <see cref="ContainerView"/>): containers a test places in a layer
    /// (<see cref="ContainerLayers.Place{TContainer}"/>) resolve by id and by content in the child and its descendants,
    /// an ancestor's entry under a shadowed id hidden; ids may be shared; a read by id skips every level that cannot
    /// own the id without touching it; a frozen context's layers never change and are read without locks, while a
    /// writable one's are read under their shard locks; equal containers a child interns at once get one id.
    /// </summary>
    public class ContainerLayersTests
    {

        static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// A store with the vector type registered at the root, as holding e-class ids, and its root environment.
        /// </summary>
        static (Store Store, ContainerEnv<VecContainer> Env) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var values = store.Root.Database.Containers;
            values.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
            return (store, values.GetEnvironment<VecContainer>());
        }

        /// <summary>
        /// Interns <paramref name="container"/> at the root.
        /// </summary>
        static uint Intern(Context root, VecContainer container) =>
            root.Database.WithExecutionState(state => state.Containers.GetOrInsert(container, state));

        /// <summary>
        /// Gets the container <paramref name="view"/> has under <paramref name="id"/>, or <c>null</c>.
        /// </summary>
        static VecContainer? ById(ContainerView view, ContainerEnv<VecContainer> env, uint id) =>
            view.TryGetContainer(env, id, out var container) ? container : null;

        /// <summary>
        /// Gets the id <paramref name="view"/> has <paramref name="container"/> under, or <c>null</c>.
        /// </summary>
        static uint? ByContent(ContainerView view, ContainerEnv<VecContainer> env, VecContainer container) =>
            view.TryGetId(env, container, out var id) ? id : null;

        /// <summary>
        /// Holds <paramref name="locks"/> on a thread of its own until the returned event is set; the caller sets it and
        /// joins the thread in a <c>finally</c>.
        /// </summary>
        static (Thread Holder, ManualResetEventSlim Release) Hold(object[] locks)
        {
            var held = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                foreach (var gate in locks)
                    Monitor.Enter(gate);

                try
                {
                    held.Set();
                    release.Wait();
                }
                finally
                {
                    for (var i = locks.Length - 1; i >= 0; i--)
                        Monitor.Exit(locks[i]);
                }
            })
            {
                IsBackground = true,
            };

            holder.Start();
            Assert.True(held.Wait(Patience), "the locks were never taken");
            return (holder, release);
        }

        [Fact]
        public void AChildResolvesItsOwnContainersByIdAndByContentAndTheRootsElsewhere()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var x = Intern(root, new VecContainer(a));
            var y = Intern(root, new VecContainer(a, a));
            var child = root.CreateChild();
            var local = child.NewId();
            child.ContainerLayers!.Place(env, new VecContainer(b), x);
            child.ContainerLayers.Place(env, new VecContainer(b, b), local);

            var rootView = ContainerView.Of(root.Database);
            var childView = ContainerView.Of(child.Database);
            Assert.True(rootView.IsRoot);
            Assert.False(childView.IsRoot);

            // The root sees its own containers only.
            Assert.Equal(new VecContainer(a), ById(rootView, env, x));
            Assert.Equal(x, ByContent(rootView, env, new VecContainer(a)));
            Assert.Null(ByContent(rootView, env, new VecContainer(b)));
            Assert.Null(ById(rootView, env, local));

            // The child sees what it placed under x in place of the root's, the root's under y, and its local container.
            Assert.Equal(new VecContainer(b), ById(childView, env, x));
            Assert.Equal(x, ByContent(childView, env, new VecContainer(b)));
            Assert.Null(ByContent(childView, env, new VecContainer(a)));
            Assert.Equal(new VecContainer(a, a), ById(childView, env, y));
            Assert.Equal(y, ByContent(childView, env, new VecContainer(a, a)));
            Assert.Equal(new VecContainer(b, b), ById(childView, env, local));
            Assert.Equal(local, ByContent(childView, env, new VecContainer(b, b)));

            // The type-erased lookups agree.
            Assert.True(childView.TryGetContainer<VecContainer>(x, out var found));
            Assert.Equal(new VecContainer(b), found);
            Assert.True(childView.TryGetId(new VecContainer(b, b), out var foundId));
            Assert.Equal(local, foundId);
            Assert.True(childView.IsContainerId(local));
            Assert.True(childView.IsContainerId(x));
            Assert.False(rootView.IsContainerId(local));
            Assert.False(childView.IsContainerId(a));

            // The root's environment is untouched, and the layer obeys the ownership rule.
            Assert.Equal(2, env.Count);
            Assert.True(child.ContainerLayers.LayerOf(env)!.Shadowed.Contains(x));
            Assert.False(child.ContainerLayers.LayerOf(env)!.Shadowed.Contains(local));
            child.CheckInvariants();
            root.CheckInvariants();
        }

        [Fact]
        public void AnIntermediateLevelShadowsAnIdForItsDescendants()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var x = Intern(root, new VecContainer(a));
            var y = Intern(root, new VecContainer(b));
            var middle = root.CreateChild();
            middle.ContainerLayers!.Place(env, new VecContainer(a, b), x);
            var leaf = middle.CreateChild();

            var middleView = ContainerView.Of(middle.Database);
            var leafView = ContainerView.Of(leaf.Database);

            // The leaf reads the middle's container under x, and the root's under y: the middle's layer does not own y.
            Assert.Equal(new VecContainer(a, b), ById(leafView, env, x));
            Assert.Equal(x, ByContent(leafView, env, new VecContainer(a, b)));
            Assert.Null(ByContent(leafView, env, new VecContainer(a)));
            Assert.Equal(new VecContainer(b), ById(leafView, env, y));

            // The leaf shadows y in turn; the middle still reads the root's container there, and the root its own.
            leaf.ContainerLayers!.Place(env, new VecContainer(b, b), y);
            Assert.Equal(new VecContainer(b, b), ById(leafView, env, y));
            Assert.Null(ByContent(leafView, env, new VecContainer(b)));
            Assert.Equal(new VecContainer(b), ById(middleView, env, y));
            Assert.Equal(y, ByContent(middleView, env, new VecContainer(b)));
            Assert.Equal(new VecContainer(b), ById(ContainerView.Of(root.Database), env, y));

            // A content the leaf places that the middle hides at the root stays the leaf's.
            Assert.Null(ByContent(middleView, env, new VecContainer(b, b)));
            leaf.CheckInvariants();
            middle.CheckInvariants();
        }

        [Fact]
        public void TwoContainersMayShareAnId()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var x = Intern(root, new VecContainer(a));
            var child = root.CreateChild();
            var layers = child.ContainerLayers!;
            layers.Place(env, new VecContainer(b), x);
            layers.Place(env, new VecContainer(b, b), x);

            // Both contents resolve to the one id, and a read by id returns one of them, as egglog's does after a
            // rebuild renamed one container's id to another's.
            var view = ContainerView.Of(child.Database);
            Assert.Equal(x, ByContent(view, env, new VecContainer(b)));
            Assert.Equal(x, ByContent(view, env, new VecContainer(b, b)));
            var read = ById(view, env, x);
            Assert.True(read is { } held && (held.Equals(new VecContainer(b)) || held.Equals(new VecContainer(b, b))));

            // CollectById copies both entries and leaves them in place, locked or not.
            var own = layers.LayerOf(env)!.Own;
            var entries = new List<ContainerEntry<VecContainer>>();
            Assert.Equal(2, own.CollectById(x, entries, locked: true));
            Assert.Equal(2, own.CollectById(x, entries, locked: false));
            Assert.Equal(4, entries.Count);
            Assert.All(entries, entry => Assert.Equal(x, entry.Id));
            Assert.Contains(entries, entry => entry.Container.Equals(new VecContainer(b)));
            Assert.Contains(entries, entry => entry.Container.Equals(new VecContainer(b, b)));
            Assert.Equal(2, own.Count);
            Assert.Equal(1, env.CollectById(x, entries, locked: true));
            Assert.Equal(0, env.CollectById(a, entries, locked: true));

            // Placing a content the layer holds already is refused, under any id.
            Assert.Throws<InvalidOperationException>(() => layers.Place(env, new VecContainer(b), child.NewId()));
            child.CheckInvariants();
        }

        [Fact]
        public void ARefusedPlaceLeavesTheViewAsItWas()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var x = Intern(root, new VecContainer(a));
            var y = Intern(root, new VecContainer(a, a));
            var child = root.CreateChild();
            var layers = child.ContainerLayers!;
            layers.Place(env, new VecContainer(b), x);

            // The layer holds (b) already, so placing it under y is refused before y is shadowed: the root's container
            // under y stays in the child's view, by id and by content.
            Assert.Throws<InvalidOperationException>(() => layers.Place(env, new VecContainer(b), y));
            Assert.False(layers.LayerOf(env)!.Shadowed.Contains(y));
            var view = ContainerView.Of(child.Database);
            Assert.Equal(new VecContainer(a, a), ById(view, env, y));
            Assert.Equal(y, ByContent(view, env, new VecContainer(a, a)));
            Assert.Equal(x, ByContent(view, env, new VecContainer(b)));
            child.CheckInvariants();
        }

        [Fact]
        public void AReadByIdSkipsEveryLevelThatCannotOwnTheId()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var x = Intern(root, new VecContainer(a));
            var y = Intern(root, new VecContainer(b));
            var middle = root.CreateChild();
            middle.ContainerLayers!.Place(env, new VecContainer(a, b), x);
            var leaf = middle.CreateChild();
            var view = ContainerView.Of(leaf.Database);

            // The middle is writable, so its layer is read under its shard locks; with them held elsewhere, a read of y
            // through the leaf still completes, since the middle's layer does not own y and is not touched, while a
            // read of x, which it owns, waits for them.
            var (holder, release) = Hold(middle.ContainerLayers.LayerOf(env)!.Own.ShardLocks);
            Task<VecContainer?> owned;
            try
            {
                var skipped = Task.Run(() => ById(view, env, y));
                Assert.True(skipped.Wait(Patience), "a read by an id the middle cannot own touched its layer");
                Assert.Equal(new VecContainer(b), skipped.Result);

                owned = Task.Run(() => ById(view, env, x));
                Assert.False(owned.Wait(Blocked), "a writable layer was read without its lock");
            }
            finally
            {
                release.Set();
                holder.Join();
            }

            Assert.True(owned.Wait(Patience));
            Assert.Equal(new VecContainer(a, b), owned.Result);
        }

        [Fact]
        public void AFrozenContextsLayersNeverChangeAndAreReadWithoutLocks()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var x = Intern(root, new VecContainer(a));
            root.Freeze();
            var middle = root.CreateChild();
            var local = middle.NewId();
            middle.ContainerLayers!.Place(env, new VecContainer(b), x);
            middle.ContainerLayers.Place(env, new VecContainer(b, b), local);
            middle.Freeze();
            Assert.True(middle.ContainerLayers.IsFrozen);
            middle.CheckInvariants();

            // Nothing is placed, shadowed or created any more.
            var layers = middle.ContainerLayers;
            var layer = layers.LayerOf(env)!;
            Assert.True(layer.Shadowed.IsFrozen);
            Assert.Throws<InvalidOperationException>(() => layers.Place(env, new VecContainer(a, a), local));
            Assert.Throws<InvalidOperationException>(() => layer.Shadowed.Add(a));
            var other = store.Root.Database.Containers;
            other.RegisterType<SetContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
            Assert.Throws<InvalidOperationException>(() => layers.GetOrCreateLayer(other.GetEnvironment<SetContainer>()));

            // With every lock of the frozen layer held elsewhere, readers of the frozen child and of its children read it
            // by id and by content.
            var leaf = middle.CreateChild();
            var (holder, release) = Hold(layer.Own.ShardLocks);
            try
            {
                var reads = new List<Task>();
                foreach (var view in new[] { ContainerView.Of(middle.Database), ContainerView.Of(leaf.Database) })
                {
                    reads.Add(Task.Run(() =>
                    {
                        for (var i = 0; i < 100; i++)
                        {
                            Assert.Equal(new VecContainer(b), ById(view, env, x));
                            Assert.Equal(new VecContainer(b, b), ById(view, env, local));
                            Assert.Equal(x, ByContent(view, env, new VecContainer(b)));
                            Assert.Equal(local, ByContent(view, env, new VecContainer(b, b)));
                            Assert.Null(ByContent(view, env, new VecContainer(a)));
                            Assert.True(view.IsContainerId(local));
                        }
                    }));
                }

                Assert.True(Task.WaitAll(reads.ToArray(), Patience), "a frozen layer was read under its locks");
            }
            finally
            {
                release.Set();
                holder.Join();
            }

            // A change to a frozen layer's own environment behind its back is reported.
            Assert.True(layer.Own.TryInsertNew(new VecContainer(a, b), new VecContainer(a, b).ComputeHash(), local));
            var error = Assert.Throws<InvalidOperationException>(() => middle.CheckInvariants());
            Assert.Contains("when the context froze", error.Message);
        }

        [Fact]
        public void TheOwnershipRuleIsChecked()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var x = Intern(root, new VecContainer(a));
            var child = root.CreateChild();
            child.ContainerLayers!.Place(env, new VecContainer(a, a), child.NewId());
            child.CheckInvariants();

            // An entry under an ancestor id the layer does not shadow breaks the rule.
            var own = child.ContainerLayers.LayerOf(env)!.Own;
            Assert.True(own.TryInsertNew(new VecContainer(a, a, a), new VecContainer(a, a, a).ComputeHash(), x));
            var error = Assert.Throws<InvalidOperationException>(() => child.CheckInvariants());
            Assert.Contains("neither local to the context nor shadowed", error.Message);
        }

#if DEBUG
        [Fact]
        public void ADebugBuildAssertsOnAClassReadThroughTheRootsViewInAnEngineCallOfAChildThatHoldsContainers()
        {
            var (store, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var x = Intern(root, new VecContainer(a));
            var plain = root.CreateChild();
            var holder = root.CreateChild();
            holder.ContainerLayers!.Place(env, new VecContainer(a, a), x);
            using var asserts = new DebugAssertScope();

            // A child whose chain holds no layer reads as the root does, through either view.
            plain.Database.WithExecutionState(state =>
            {
                ContainerView.EnterEngineCall(state);
                try
                {
                    Assert.Equal(new VecContainer(a), ById(default, env, x));
                    Assert.Equal(x, ByContent(default, env, new VecContainer(a)));
                }
                finally
                {
                    ContainerView.ExitEngineCall(state);
                }

                return 0;
            });

            // In a child that holds containers, its engine call reads through its own view, and the root's view is wrong.
            holder.Database.WithExecutionState(state =>
            {
                ContainerView.EnterEngineCall(state);
                try
                {
                    Assert.Equal(new VecContainer(a, a), ById(state.Containers, env, x));
                    Assert.Throws<DebugAssertFailedException>(() => ById(default, env, x));
                    Assert.Throws<DebugAssertFailedException>(() => ByContent(default, env, new VecContainer(a)));
                }
                finally
                {
                    ContainerView.ExitEngineCall(state);
                }

                // Outside an engine call nothing is asserted.
                Assert.Equal(new VecContainer(a), ById(default, env, x));
                return 0;
            });
        }
#endif

        /// <summary>
        /// A child's rule tasks interning equal containers at once, as a parallel run's actions do
        /// (<see cref="ContainerLayers.GetOrInsert{TContainer}"/>): each content gets one id, local to the child, from
        /// every thread, its own layer holds each once, and the layer's ids are minted only for contents that won, so
        /// the child's layer and its invariants are as a serial run's would leave them. The child is one of a writable
        /// root that runs in parallel, so its layer's own environment has several shards; the threads start together
        /// and run to completion, so nothing waits on a bound.
        /// </summary>
        [Fact]
        public void EqualContainersInternedAtOnceInOneChildGetOneId()
        {
            var store = new Store(new ParallelHeuristics(4));
            var values = store.Root.Database.Containers;
            values.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
            var env = values.GetEnvironment<VecContainer>();
            var root = store.Root;
            var elements = new uint[64];
            for (var i = 0; i < elements.Length; i++)
                elements[i] = root.NewId();

            var child = root.CreateChild();
            var layers = child.ContainerLayers!;
            const int Threads = 8;
            var ids = new uint[Threads][];
            Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t =>
            {
                var mine = new uint[elements.Length];
                for (var i = 0; i < elements.Length; i++)
                {
                    // Each thread walks the contents in its own order.
                    var e = (i + t * 13) % elements.Length;
                    mine[e] = layers.GetOrInsert(env, new VecContainer(elements[e], elements[(e + 1) % elements.Length]));
                }

                ids[t] = mine;
            });

            var own = layers.LayerAt(env.TypeId)!.OwnBase;
            Assert.Equal(elements.Length, own.Count);
            Assert.Equal(0, env.Count);
            Assert.Equal(elements.Length, child.IdBlocks!.Minted);

            var distinct = new HashSet<uint>();
            for (var i = 0; i < elements.Length; i++)
            {
                for (var t = 1; t < Threads; t++)
                    Assert.Equal(ids[0][i], ids[t][i]);

                Assert.True(child.IsLocalId(ids[0][i]));
                Assert.True(distinct.Add(ids[0][i]));
                Assert.Equal(new VecContainer(elements[i], elements[(i + 1) % elements.Length]), ById(ContainerView.Of(child.Database), env, ids[0][i]));
            }

            child.Rebuild();
            child.CheckInvariants();
            child.Dispose();
            root.CheckInvariants();
        }

        [Fact]
        public void TheShadowedIdsGrowAndAreReadFromAnyThread()
        {
            var set = new ShadowSet();
            Assert.False(set.Contains(7));
            Assert.Equal(0, set.Count);

            // The domain thread adds while other threads read: an id once added is always found.
            const int Ids = 5000;
            var added = 0;
            using var stop = new CancellationTokenSource();
            var readers = new Task[3];
            for (var r = 0; r < readers.Length; r++)
            {
                readers[r] = Task.Run(() =>
                {
                    while (stop.IsCancellationRequested == false)
                    {
                        var known = Volatile.Read(ref added);
                        for (var id = 0u; id < known; id++)
                            Assert.True(set.Contains(id * 7 + 1), $"id {id * 7 + 1} was added and is not found");
                    }
                });
            }

            for (var id = 0u; id < Ids; id++)
            {
                Assert.True(set.Add(id * 7 + 1));
                Volatile.Write(ref added, (int)id + 1);
            }

            stop.Cancel();
            Task.WaitAll(readers);
            Assert.Equal(Ids, set.Count);
            Assert.False(set.Add(1));
            Assert.False(set.Contains(2));

            var copied = new List<uint>();
            set.CopyTo(copied);
            Assert.Equal(Ids, copied.Count);

            set.Freeze();
            Assert.True(set.IsFrozen);
            Assert.True(set.Contains(1));
            Assert.Throws<InvalidOperationException>(() => set.Add(3));
        }

    }

}
