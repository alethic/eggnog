using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a light child costs, since a child pays in proportion to what it touches: the bytes a bare child and an
    /// empty layer allocate, measured on this thread after a warm-up, stay under the bounds the lazy pending queues,
    /// lazy notification flags and lazy index-catalog gates bring them to; a child whose own losers no container holds
    /// makes no container-pass storage, a container pass that claims nothing allocates nothing with that storage or
    /// without it, and a frozen or disposed child keeps none; and the root's tables keep their eager queues.
    /// </summary>
    /// <remarks>
    /// The bounds have headroom over the Release figures (about 2.5 KB and 1.1 KB, <c>--context-footprint</c> of the
    /// benchmark app) for the DEBUG-only bookkeeping of the test build. Without the lazy queues, flags and gates the
    /// same workloads allocate about 4.7 KB and 2.8 KB, mostly two
    /// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> segments per pending state.
    /// </remarks>
    [Trait("Category", "Performance")]
    [Collection(AllocationProbeCollection.Name)]
    public class ChildFootprintTests
    {

        const int Warmup = 50;
        const int Repetitions = 100;

        /// <summary>
        /// The windows of <see cref="Repetitions"/> calls <see cref="Measure"/> takes.
        /// </summary>
        const int Windows = 5;

        /// <summary>
        /// Runs <paramref name="action"/> to warm the pools, then returns the bytes one call allocates on this thread:
        /// the least, over <see cref="Windows"/> windows, of a window's bytes divided by its <see cref="Repetitions"/>
        /// calls.
        /// </summary>
        /// <remarks>
        /// A child rents its storage from the shared array pool, which the runtime trims after a full collection under
        /// memory pressure, so on a loaded machine one window can rent fresh arrays: a single window of 500 calls once
        /// read 1,760 B for an empty layer on an osx-x64 runner, against about 1,190 B elsewhere (Debug, net10.0). An
        /// allocation of every call shows in every window, so the least still catches it.
        /// </remarks>
        static double Measure(Action action)
        {
            for (var i = 0; i < Warmup; i++)
                action();

            var least = long.MaxValue;
            for (var window = 0; window < Windows; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < Repetitions; i++)
                    action();

                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return least / (double)Repetitions;
        }

        static (Store Store, TableId F) FrozenRoot()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            store.AddTable(Relation(2), name: "r");
            var root = store.Root;
            var ids = new uint[64];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = root.NewId();

            root.Write(state =>
            {
                foreach (var id in ids)
                    state.StageInsert(f, [id, 1, state.Timestamp]);

                return 0;
            });
            root.Freeze();
            return (store, f);
        }

        /// <summary>
        /// Creating and disposing a child of a frozen root allocates under 3,200 bytes, and creating an empty layer in
        /// it under 1,600 more.
        /// </summary>
        [Fact]
        public void ABareChildAndAnEmptyLayerStayLight()
        {
            var (store, f) = FrozenRoot();
            var bare = Measure(() => store.Root.CreateChild().Dispose());
            var layered = Measure(() =>
            {
                var child = store.Root.CreateChild();
                child.Tables!.GetOrCreateLayer(f);
                child.Dispose();
            });

            Assert.True(bare < 3_200, $"a bare child allocated {bare:N0} B");
            Assert.True(layered - bare < 1_600, $"an empty layer allocated {layered - bare:N0} B");
        }

        /// <summary>
        /// A container pass whose own losers no container holds allocates nothing, with a type of classes registered
        /// and holding a container: before its child has the pass's storage, it finds that no container below holds a
        /// loser and makes none; once the child has it, made here by a pass over a dirty id inherited from an ancestor,
        /// it probes the element indexes for each loser, but makes no execution state, canonicalizer or summary, and
        /// reuses its storage. Each call replays the child's journal of 63 own losers by moving its container mark back.
        /// </summary>
        [Fact]
        public void AContainerPassThatClaimsNothingAllocatesNothing()
        {
            var store = new Store(ParallelHeuristics.Serial);
            store.Root.Database.Containers.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
            var root = store.Root;
            var held = root.NewId();
            var h = root.Database.WithExecutionState(state => state.Containers.GetOrInsert(new VecContainer(held), state));
            var ids = new uint[64];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = root.NewId();

            root.Freeze();
            using var child = root.CreateChild();
            for (var i = 1; i < ids.Length; i++)
                child.Union(ids[0], ids[i]);

            child.Rebuild();
            var layers = child.ContainerLayers!;
            var end = layers.ContainerMark;
            Assert.Equal(ids.Length - 1, end);
            Assert.True(layers.IsEmpty);
            Assert.False(layers.HasPassStorage);

            var before = Measure(() =>
            {
                layers.ContainerMark = 0;
                layers.RebuildPass(1, ReadOnlySpan<uint>.Empty);
            });

            Assert.False(layers.HasPassStorage);
            layers.RebuildPass(1, [h]);
            Assert.True(layers.HasPassStorage);
            var after = Measure(() =>
            {
                layers.ContainerMark = 0;
                layers.RebuildPass(1, ReadOnlySpan<uint>.Empty);
            });

            Assert.Equal(end, layers.ContainerMark);
            Assert.True(layers.IsEmpty);
            Assert.True(before < 8, $"a container pass that claims nothing allocated {before:N0} B before the pass's storage");
            Assert.True(after < 8, $"a container pass that claims nothing allocated {after:N0} B with the pass's storage");
        }

        /// <summary>
        /// A frozen root with two classes no container holds, and, when <paramref name="withType"/>, a type of classes
        /// registered and holding a container of a third.
        /// </summary>
        static (Store Store, uint A, uint B) RootWithLoneClasses(bool withType)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            if (withType)
            {
                root.Database.Containers.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
                var held = root.NewId();
                root.Database.WithExecutionState(state => state.Containers.GetOrInsert(new VecContainer(held), state));
            }

            var a = root.NewId();
            var b = root.NewId();
            root.Freeze();
            return (store, a, b);
        }

        /// <summary>
        /// A child of a frozen root that unions two root classes no container holds and rebuilds makes no
        /// container-pass storage, with a type of classes registered and holding a container: the pass finds that no
        /// container below holds or is its loser before it makes any. Such a child allocates under 512 B more than the
        /// same child without the type, where the storage alone is about 1.1 KB; the rest is the DEBUG check of its
        /// view of containers at the end of its rebuild.
        /// </summary>
        [Fact]
        public void AChildWhoseLosersNoContainerHoldsMakesNoPassStorage()
        {
            var (typed, a, b) = RootWithLoneClasses(withType: true);
            using (var child = typed.Root.CreateChild())
            {
                child.Union(a, b);
                child.Rebuild();
                Assert.True(child.IsClean);
                Assert.False(child.ContainerLayers!.HasPassStorage);
            }

            var (plain, pa, pb) = RootWithLoneClasses(withType: false);
            var without = Measure(() =>
            {
                var child = plain.Root.CreateChild();
                child.Union(pa, pb);
                child.Rebuild();
                child.Dispose();
            });
            var with = Measure(() =>
            {
                var child = typed.Root.CreateChild();
                child.Union(a, b);
                child.Rebuild();
                child.Dispose();
            });

            Assert.True(with - without < 512, $"a child whose losers no container holds allocated {with - without:N0} B more with a type of classes");
        }

        /// <summary>
        /// A child that claimed and rebuilt a container lets go of its container passes' storage when it freezes, since
        /// a frozen context never rebuilds again, and so does one disposed while still referenced; the frozen child's
        /// view keeps its copy.
        /// </summary>
        [Fact]
        public void AFrozenOrDisposedChildKeepsNoPassStorage()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            root.Database.Containers.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
            var k = root.NewId();
            var u = root.NewId();
            var h = root.Database.WithExecutionState(state => state.Containers.GetOrInsert(new VecContainer(u), state));
            root.Freeze();

            using var frozen = root.CreateChild();
            frozen.Union(k, u);
            frozen.Rebuild();
            Assert.True(frozen.ContainerLayers!.HasPassStorage);
            frozen.Freeze();
            Assert.False(frozen.ContainerLayers.HasPassStorage);
            Assert.True(ContainerView.Of(frozen.Database).TryGetContainer(root.Database.Containers.GetEnvironment<VecContainer>(), h, out var copy));
            Assert.Equal(new VecContainer(k), copy);

            var disposed = root.CreateChild();
            disposed.Union(k, u);
            disposed.Rebuild();
            Assert.True(disposed.ContainerLayers!.HasPassStorage);
            disposed.Dispose();
            Assert.False(disposed.ContainerLayers.HasPassStorage);
        }

        /// <summary>
        /// A child's layer and union-find table create their pending queues only when they stage; a root's are eager.
        /// </summary>
        [Fact]
        public void OnlyChildTablesDeferTheirQueues()
        {
            var (store, f) = FrozenRoot();
            using var child = store.Root.CreateChild();
            var layer = child.Tables!.GetOrCreateLayer(f).Own;
            var other = store.Root.CreateChild();
            var second = other.Tables!.GetOrCreateLayer(f).Own;
            Assert.Same(layer.Pending.RowQueue(0), second.Pending.RowQueue(0));
            other.Dispose();

            var root = (SortedWritesTable)store.Root.Database.GetTable(f);
            Assert.NotSame(root.Pending.RowQueue(0), layer.Pending.RowQueue(0));

            // The first write creates the child's queue and lands.
            var shared = layer.Pending.RowQueue(0);
            Insert(child, f, 5, 3, 0);
            Assert.NotSame(shared, layer.Pending.RowQueue(0));
            Assert.True(shared.IsEmpty);
            Assert.Equal(1L, child.Database.TotalSizeEstimate);
        }

    }

}
