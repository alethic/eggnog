using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child's container pass (<see cref="ContainerLayers.RebuildPass"/>), storage part. A child whose own union
    /// displaces an element of an ancestor's container of classes claims the container and rebuilds its copy in its
    /// container layers, as egglog rebuilds the copy its push makes: containers that become equal anywhere in the
    /// child's view merge with egglog's tie-break, the smaller id; the rows naming a container the child rebuilt in
    /// place are re-stamped, its own by refresh and the ancestors' by hide and copy, closed over the containers that
    /// hold it, and its descendants learn of them from its container log. Claimed containers collide and merge as one
    /// hash-cons does, incrementally over a layer of more than 1000 containers and in full over a smaller one. Siblings
    /// under a frozen root pass concurrently, and a frozen child's claims are read from any thread. The probes the
    /// expected values come from ran on egglog's binary at the engine's pin, <c>5e655cae</c>.
    /// </summary>
    public class ContainerPassTests
    {

        /// <summary>
        /// A serial store with the vector type registered at the root, as holding e-class ids, under the bridge's
        /// merge; a function <c>f(id) → number</c> merged by the larger value; and the root's environment of vectors.
        /// </summary>
        static (Store Store, TableId F, ContainerEnv<VecContainer> Env) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var values = store.Root.Database.Containers;
            values.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
            return (store, f, values.GetEnvironment<VecContainer>());
        }

        /// <summary>
        /// Interns a vector of <paramref name="elements"/> at the root.
        /// </summary>
        static uint Intern(Context root, params uint[] elements) =>
            root.Database.WithExecutionState(state => state.Containers.GetOrInsert(new VecContainer(elements), state));

        /// <summary>
        /// Gets the vector <paramref name="context"/> sees under <paramref name="id"/>, or <c>null</c>.
        /// </summary>
        static VecContainer? ById(Context context, ContainerEnv<VecContainer> env, uint id) =>
            ContainerView.Of(context.Database).TryGetContainer(env, id, out var container) ? container : null;

        /// <summary>
        /// Gets the id under which <paramref name="context"/> sees the vector of <paramref name="elements"/>, or
        /// <c>null</c>.
        /// </summary>
        static uint? ByContent(Context context, ContainerEnv<VecContainer> env, params uint[] elements) =>
            ContainerView.Of(context.Database).TryGetId(env, new VecContainer(elements), out var id) ? id : null;

        /// <summary>
        /// Gets the visible rows of <paramref name="table"/> in <paramref name="context"/> as (key, value) pairs.
        /// </summary>
        static List<(uint Key, uint Value)> Rows(Context context, TableId table) =>
            ScanVisible(context.GetView(table)).Select(r => (r.Cells[0], r.Cells[1])).OrderBy(r => r.Item1).ToList();

        /// <summary>
        /// Gets the row id of the root's row of <paramref name="table"/> under <paramref name="key"/>.
        /// </summary>
        static int RootRowOf(Context root, TableId table, uint key) =>
            ScanVisible(root.GetView(table)).Single(r => r.Cells[0] == key).Row;

        /// <summary>
        /// Asserts that every id the child's layer of vectors holds an entry under holds exactly one: a read by id
        /// returns one content.
        /// </summary>
        static void AssertOneContentPerId(Context child, ContainerEnv<VecContainer> env)
        {
            var own = ((ContainerLayer<VecContainer>)child.ContainerLayers!.LayerAt(env.TypeId)!).Own;
            var entries = new List<ContainerEntry<VecContainer>>();
            var collector = new ContainerEntryCollector<VecContainer>(entries);
            own.ForEach(ref collector);
            var under = new List<ContainerEntry<VecContainer>>();
            foreach (var entry in entries)
            {
                under.Clear();
                Assert.Equal(1, own.CollectById(entry.Id, under, locked: true));
            }
        }

        /// <summary>
        /// egglog's p2 in a child (probe <c>p2-push.egg</c>). With <c>a &lt; b</c> the root holds <c>vb = [b]</c> and
        /// <c>va = [a]</c>, <c>vb</c> the smaller id, and <c>f(va) = 1</c>, <c>f(vb) = 2</c> under a max merge. The
        /// child's union of <c>a</c> and <c>b</c> claims <c>vb</c>, which holds its loser, and rebuilds it to
        /// <c>[a]</c>, which meets the root's <c>va</c>: the smaller <c>vb</c> wins, <c>va</c> is claimed and merged
        /// into it, and the child's two rows of <c>f</c> become one, <c>f(vb) = 2</c>. egglog prints the one row <c>(f
        /// (vec-of (A))) -&gt; 2</c> in the push and both rows after the pop; the root keeps both vectors and rows.
        /// </summary>
        [Fact]
        public void TwoContainersThatBecomeEqualInAChildKeepTheSmallerIdAsEgglogs()
        {
            var (store, f, env) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var vb = Intern(root, b);
            var va = Intern(root, a);
            Insert(root, f, va, 1, 0);
            Insert(root, f, vb, 2, 0);
            root.Freeze();

            var child = root.CreateChild();
            child.Union(a, b);
            Assert.True(child.Rebuild());
            Assert.Equal(vb, child.Find(va));
            Assert.Equal(new VecContainer(a), ById(child, env, vb));
            Assert.Null(ById(child, env, va));
            Assert.Equal(vb, ByContent(child, env, a));
            Assert.Null(ByContent(child, env, b));
            Assert.Equal([(vb, 2u)], Rows(child, f));
            var layer = child.ContainerLayers!.LayerAt(env.TypeId)!;
            Assert.True(layer.Shadowed.Contains(va));
            Assert.True(layer.Shadowed.Contains(vb));
            AssertOneContentPerId(child, env);
            child.CheckInvariants();
            child.Dispose();

            Assert.False(root.AreEqual(va, vb));
            Assert.True(env.TryGetContainer(va, out var rootA));
            Assert.Equal(new VecContainer(a), rootA);
            Assert.True(env.TryGetContainer(vb, out var rootB));
            Assert.Equal(new VecContainer(b), rootB);
            Assert.Equal([(vb, 2u), (va, 1u)], Rows(root, f).OrderBy(r => r.Key == vb ? 0 : 1).ToList());
        }

        /// <summary>
        /// Rows on claimed ids. The child's own row under <c>va</c>, a container it claims and merges away, moves to
        /// the winner <c>vb</c> and meets the root's row there; its own row under <c>vb</c>, a container it rebuilds in
        /// place, is re-stamped. The root's row under <c>vb</c> is copied into the child with the pass's epoch and
        /// hidden there, refreshed, as it names an id whose meaning changed and none that is stale; the root keeps it.
        /// </summary>
        [Fact]
        public void RowsOnClaimedIdsFollowTheContainersMergesAndAreRestamped()
        {
            var (store, f, env) = Setup();
            var g = store.AddTable(MaxFunction(1), name: "g");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var vb = Intern(root, b);
            var va = Intern(root, a);
            Insert(root, f, vb, 2, 0);
            root.Freeze();
            var rootStamp = Row(root, f, vb)![2];

            var child = root.CreateChild();
            Insert(child, f, va, 7, 0);
            Insert(child, g, vb, 3, 0);
            var ownStamp = Row(child, g, vb)![2];
            child.Union(a, b);
            child.Rebuild();

            Assert.Equal([(vb, 7u)], Rows(child, f));
            Assert.Null(Row(child, f, va));
            var restamped = Row(child, g, vb)!;
            Assert.Equal(3u, restamped[1]);
            Assert.True(restamped[2] > ownStamp, "the child's own row naming a container it rebuilt in place was re-stamped");
            Assert.True(Row(child, f, vb)![2] > rootStamp);
#if DEBUG
            Assert.Equal(HideReason.Refreshed, child.Tables!.TryGetLayer(f)!.ReasonFor(0, RootRowOf(root, f, vb)));
#endif
            child.CheckInvariants();
            Assert.Equal([(vb, 2u)], Rows(root, f));
        }

        /// <summary>
        /// The core of probe <c>ov1-root-row-child-dirty.egg</c>, storage part: a root row naming a root container the
        /// child rebuilds in place is copied into the child with the pass's epoch and hidden there, so the child's
        /// semi-naive evaluation sees the row's new meaning as new, while the root's row keeps its stamp. With a live
        /// grandchild the child's container log is active, its Dirty stream gains the container, and the grandchild's
        /// rebuild re-stamps its own row naming it and reads the child's copy.
        /// </summary>
        [Fact]
        public void ARootRowNamingAContainerTheChildRebuiltInPlaceIsRefreshedThere()
        {
            var (store, f, env) = Setup();
            var g = store.AddTable(MaxFunction(1), name: "g");
            var root = store.Root;
            var k = root.NewId();
            var w = root.NewId();
            var v = Intern(root, w);
            Insert(root, f, v, 5, 0);
            root.Freeze();
            var rootStamp = Row(root, f, v)![2];

            var child = root.CreateChild();
            var grandchild = child.CreateChild();
            Insert(grandchild, g, v, 6, 0);
            var grandStamp = Row(grandchild, g, v)![2];
            child.Union(k, w);
            child.Rebuild();

            Assert.Equal(new VecContainer(k), ById(child, env, v));
            var copy = Row(child, f, v)!;
            Assert.Equal(5u, copy[1]);
            Assert.True(copy[2] > rootStamp, "the root's row naming the container the child rebuilt in place was re-stamped in the child");
            Assert.Equal(rootStamp, Row(root, f, v)![2]);
#if DEBUG
            Assert.Equal(HideReason.Refreshed, child.Tables!.TryGetLayer(f)!.ReasonFor(0, RootRowOf(root, f, v)));
#endif
            var log = child.ContainerLayers!.Log;
            Assert.True(log.Active);
            Assert.Equal(1, log.DirtyCount);
            child.CheckInvariants();

            grandchild.Rebuild();
            Assert.Equal(new VecContainer(k), ById(grandchild, env, v));
            Assert.Equal(k, grandchild.Find(w));
            var own = Row(grandchild, g, v)!;
            Assert.Equal(6u, own[1]);
            Assert.True(own[2] > grandStamp, "the grandchild's own row naming the container its parent rebuilt in place was re-stamped");
            Assert.Equal(5u, Row(grandchild, f, v)![1]);
            Assert.True(grandchild.ContainerLayers!.IsEmpty);
            grandchild.CheckInvariants();
            grandchild.Dispose();
            Assert.False(log.Active);
            Assert.Equal(0, log.DirtyCount);
        }

        /// <summary>
        /// The dirty closure over the child's view (probe <c>judge-nested-closure.egg</c>, storage part): the root
        /// holds <c>inner = [w]</c>, <c>outer = [inner]</c> and <c>top = [outer]</c>, each named by a row. The child's
        /// union of <c>k</c> and <c>w</c> claims and rebuilds <c>inner</c> in place; <c>outer</c> and <c>top</c> hold
        /// it directly and transitively, so their meaning changed too and their rows are re-stamped in the child, but
        /// neither is claimed, since neither holds a stale id.
        /// </summary>
        [Fact]
        public void TheContainersHoldingAContainerRebuiltInPlaceAreDirtyButNotClaimed()
        {
            var (store, f, env) = Setup();
            var root = store.Root;
            var k = root.NewId();
            var w = root.NewId();
            var other = Intern(root, k);
            var inner = Intern(root, w);
            var outer = Intern(root, inner);
            var top = Intern(root, outer);
            Insert(root, f, inner, 1, 0);
            Insert(root, f, outer, 2, 0);
            Insert(root, f, top, 3, 0);
            Insert(root, f, other, 4, 0);
            root.Freeze();
            var stamps = new[] { inner, outer, top, other }.ToDictionary(id => id, id => Row(root, f, id)![2]);

            var child = root.CreateChild();
            child.Union(k, w);
            child.Rebuild();

            // [w] is now [k], the content of other, whose smaller id wins: inner merges into other.
            Assert.True(other < inner);
            Assert.Equal(other, child.Find(inner));
            Assert.Equal(new VecContainer(k), ById(child, env, other));
            Assert.Equal(other, ByContent(child, env, k));
            Assert.Null(ById(child, env, inner));

            // outer = [inner] holds a stale id now, so it is claimed and rebuilt to [other]; top holds outer, rebuilt
            // in place, so it is dirty, and its row is re-stamped, but it is not claimed.
            Assert.Equal(new VecContainer(other), ById(child, env, outer));
            Assert.Equal(new VecContainer(outer), ById(child, env, top));
            var layer = child.ContainerLayers!.LayerAt(env.TypeId)!;
            Assert.True(layer.Shadowed.Contains(outer));
            Assert.False(layer.Shadowed.Contains(top));
            Assert.True(Row(child, f, top)![2] > stamps[top], "the row of a container holding one the child rebuilt in place was re-stamped");
            Assert.True(Row(child, f, outer)![2] > stamps[outer]);
            Assert.Equal(4u, Row(child, f, other)![1]);
            child.CheckInvariants();
        }

        /// <summary>
        /// A nested container dirty only by closure, with no merge: the root holds <c>inner = [w]</c> and <c>outer =
        /// [inner]</c>, and no container <c>[k]</c>. The child rebuilds <c>inner</c> in place to <c>[k]</c>;
        /// <c>outer</c> keeps its id and contents, so it is neither claimed nor rebuilt, but its root row is re-stamped
        /// in the child.
        /// </summary>
        [Fact]
        public void AContainerDirtyOnlyByClosureKeepsItsEntryAndIsRestamped()
        {
            var (store, f, env) = Setup();
            var root = store.Root;
            var k = root.NewId();
            var w = root.NewId();
            var inner = Intern(root, w);
            var outer = Intern(root, inner);
            Insert(root, f, outer, 2, 0);
            root.Freeze();
            var stamp = Row(root, f, outer)![2];

            var child = root.CreateChild();
            child.Union(k, w);
            child.Rebuild();

            Assert.Equal(new VecContainer(k), ById(child, env, inner));
            Assert.Equal(new VecContainer(inner), ById(child, env, outer));
            var layer = child.ContainerLayers!.LayerAt(env.TypeId)!;
            Assert.True(layer.Shadowed.Contains(inner));
            Assert.False(layer.Shadowed.Contains(outer));
            Assert.True(Row(child, f, outer)![2] > stamp);
#if DEBUG
            Assert.Equal(HideReason.Refreshed, child.Tables!.TryGetLayer(f)!.ReasonFor(0, RootRowOf(root, f, outer)));
#endif
            child.CheckInvariants();
        }

        /// <summary>
        /// Claimed containers collide in a child. The root holds, for each pair, <c>P = [a, t1]</c> and <c>Q = [b,
        /// t1]</c>, and, apart, <c>R = [c]</c> and <c>S = [d]</c>, with the smaller id alternating between the two of a
        /// pair, and rows naming them all. The child's first rebuild displaces <c>t1</c> and claims every <c>P</c> and
        /// <c>Q</c>, rebuilt in place; its second displaces each <c>b</c> and <c>d</c>, so each claimed <c>Q</c> meets
        /// its claimed <c>P</c> in the child's layer, and each <c>S</c>, claimed in that pass, meets the root's
        /// <c>R</c>: the smaller id keeps the content, the other is merged away, and the rows meet. With
        /// <paramref name="large"/>, 1100 more claimed containers make the layer large enough for egglog's incremental
        /// rebuild (more than 1000 containers), which revisits only what the second rebuild's displacements reach;
        /// without, the layer is rebuilt in full. Either way a read by id returns one content, and the invariants hold.
        /// </summary>
        /// <param name="large">Whether the layer holds more than 1000 containers at the second rebuild.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ClaimedContainersThatCollideMergeIntoTheSmallerId(bool large)
        {
            const int pairs = 12;
            var (store, f, env) = Setup();
            var root = store.Root;
            var t0 = root.NewId();
            var t1 = root.NewId();
            var p = new (uint A, uint B, uint P, uint Q)[pairs];
            var s = new (uint C, uint D, uint R, uint S)[pairs];
            for (var i = 0; i < pairs; i++)
            {
                var a = root.NewId();
                var b = root.NewId();
                var c = root.NewId();
                var d = root.NewId();
                uint vp, vq, vr, vs;
                if (i % 2 == 0)
                {
                    vp = Intern(root, a, t1);
                    vq = Intern(root, b, t1);
                    vr = Intern(root, c);
                    vs = Intern(root, d);
                }
                else
                {
                    vq = Intern(root, b, t1);
                    vp = Intern(root, a, t1);
                    vs = Intern(root, d);
                    vr = Intern(root, c);
                }

                p[i] = (a, b, vp, vq);
                s[i] = (c, d, vr, vs);
                Insert(root, f, vp, 1, 0);
                Insert(root, f, vq, 2, 0);
                Insert(root, f, vr, 3, 0);
                Insert(root, f, vs, 4, 0);
            }

            var padding = large ? 1100 : 0;
            for (var m = 0; m < padding; m++)
                Intern(root, root.NewId(), t1);

            root.Freeze();

            var child = root.CreateChild();
            child.Union(t0, t1);
            child.Rebuild();
            var own = ((ContainerLayer<VecContainer>)child.ContainerLayers!.LayerAt(env.TypeId)!).Own;
            Assert.Equal(2 * pairs + padding, own.Count);

            foreach (var (a, b, _, _) in p)
                child.Union(a, b);

            foreach (var (c, d, _, _) in s)
                child.Union(c, d);

            VecContainer.TryRebuildCalls = 0;
            child.Rebuild();
            var rebuildCalls = VecContainer.TryRebuildCalls;
            child.CheckInvariants();
#if DEBUG
            // A DEBUG build checks the invariants at the end of every rebuild, which rebuilds every container of the
            // layer once more to check it is canonical.
            rebuildCalls -= VecContainer.TryRebuildCalls - rebuildCalls;
#endif
            if (large)
                Assert.True(rebuildCalls < 10 * pairs, $"the incremental rebuild revisited {rebuildCalls} containers");
            else
                Assert.True(rebuildCalls >= 3 * pairs, $"the full rebuild revisited {rebuildCalls} containers");

            foreach (var (a, b, vp, vq) in p)
            {
                var (winner, loser) = vp < vq ? (vp, vq) : (vq, vp);
                Assert.Equal(winner, child.Find(loser));
                Assert.Equal(new VecContainer(a, t0), ById(child, env, winner));
                Assert.Null(ById(child, env, loser));
                Assert.Equal(winner, ByContent(child, env, a, t0));
                Assert.Null(ByContent(child, env, b, t1));
                Assert.Null(ByContent(child, env, b, t0));
                Assert.Equal(2u, Row(child, f, winner)![1]);
                Assert.Null(Row(child, f, loser));
            }

            foreach (var (c, d, vr, vs) in s)
            {
                var (winner, loser) = vr < vs ? (vr, vs) : (vs, vr);
                Assert.Equal(winner, child.Find(loser));
                Assert.Equal(new VecContainer(c), ById(child, env, winner));
                Assert.Null(ById(child, env, loser));
                Assert.Equal(winner, ByContent(child, env, c));
                Assert.Null(ByContent(child, env, d));
                Assert.Equal(4u, Row(child, f, winner)![1]);
                Assert.Null(Row(child, f, loser));
            }

            AssertOneContentPerId(child, env);
            Assert.Equal(pairs + padding + s.Count(x => x.S < x.R), own.Count);

            // The root holds what it held.
            foreach (var (a, b, vp, vq) in p)
            {
                Assert.False(root.AreEqual(vp, vq));
                Assert.True(env.TryGetContainer(vq, out var rootQ));
                Assert.Equal(new VecContainer(b, t1), rootQ);
            }
        }

        /// <summary>
        /// A grandchild claims from its parent's layer: the child claims <c>v = [w, u]</c> and rebuilds it to <c>[k,
        /// u]</c>; the grandchild's union of <c>m</c> and <c>u</c> claims <c>v</c> again, reading the child's copy, the
        /// nearest level that owns the id, and rebuilds it to <c>[k, m]</c>, and claims the root's <c>x = [u]</c> as
        /// well. Each level sees its own copy, under a frozen or a writable parent.
        /// </summary>
        /// <param name="frozenParent">Whether the child is frozen before the grandchild is created.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AGrandchildClaimsTheNearestLevelsCopy(bool frozenParent)
        {
            var (store, _, env) = Setup();
            var root = store.Root;
            var k = root.NewId();
            var w = root.NewId();
            var m = root.NewId();
            var u = root.NewId();
            var v = Intern(root, w, u);
            var x = Intern(root, u);
            root.Freeze();

            var child = root.CreateChild();
            child.Union(k, w);
            child.Rebuild();
            if (frozenParent)
                child.Freeze();

            var grandchild = child.CreateChild();
            grandchild.Union(m, u);
            grandchild.Rebuild();

            Assert.Equal(new VecContainer(k, m), ById(grandchild, env, v));
            Assert.Equal(new VecContainer(m), ById(grandchild, env, x));
            Assert.Equal(v, ByContent(grandchild, env, k, m));
            Assert.Null(ByContent(grandchild, env, k, u));
            Assert.Null(ByContent(grandchild, env, w, u));
            Assert.Equal(new VecContainer(k, u), ById(child, env, v));
            Assert.Equal(new VecContainer(u), ById(child, env, x));
            Assert.True(env.TryGetContainer(v, out var original));
            Assert.Equal(new VecContainer(w, u), original);
            grandchild.CheckInvariants();
            grandchild.Dispose();
            child.CheckInvariants();
        }

        /// <summary>
        /// Siblings under a frozen root pass concurrently, each on a thread of its own: every child displaces the one
        /// id all the root's containers hold, so each claims every container, reading the root's environment while the
        /// others do, and merges two of them its own union makes equal. Each sees only its own copies, and the root is
        /// unchanged.
        /// </summary>
        [Fact]
        public void SiblingsUnderAFrozenRootPassConcurrently()
        {
            const int siblings = 8;
            const int vectors = 64;
            var (store, f, env) = Setup();
            var root = store.Root;
            var t0 = root.NewId();
            var t1 = root.NewId();
            var heads = Enumerable.Range(0, vectors).Select(_ => root.NewId()).ToArray();
            var ids = heads.Select(h => Intern(root, h, t1)).ToArray();
            foreach (var id in ids)
                Insert(root, f, id, 1, 0);

            root.Freeze();
            var children = Enumerable.Range(0, siblings).Select(_ => root.CreateChild()).ToArray();
            using var start = new Barrier(siblings);
            var tasks = Enumerable.Range(0, siblings).Select(i => Task.Factory.StartNew(() =>
            {
                var child = children[i];
                start.SignalAndWait();
                child.Union(t0, t1);
                child.Union(heads[2 * i], heads[2 * i + 1]);
                child.Rebuild();
            }, TaskCreationOptions.LongRunning)).ToArray();
            Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(60)));

            for (var i = 0; i < siblings; i++)
            {
                var child = children[i];
                var (winner, loser) = (ids[2 * i], ids[2 * i + 1]);
                Assert.Equal(winner, child.Find(loser));
                Assert.Equal(new VecContainer(heads[2 * i], t0), ById(child, env, winner));
                Assert.Null(ById(child, env, loser));
                for (var j = 0; j < vectors; j++)
                {
                    if (j == 2 * i || j == 2 * i + 1)
                        continue;

                    Assert.Equal(new VecContainer(heads[j], t0), ById(child, env, ids[j]));
                    Assert.Equal(ids[j], ByContent(child, env, heads[j], t0));
                }

                Assert.Equal(vectors - 1, Rows(child, f).Count);
                child.CheckInvariants();
            }

            for (var j = 0; j < vectors; j++)
            {
                Assert.True(env.TryGetContainer(ids[j], out var original));
                Assert.Equal(new VecContainer(heads[j], t1), original);
            }

            foreach (var child in children)
                child.Dispose();

            root.CheckInvariants();
        }

        /// <summary>
        /// A frozen child's claims are read from any thread: the child claims and rebuilds 200 containers, freezes, and
        /// eight threads, each through the frozen child and through a grandchild of its own, read every copy by id and
        /// by content at once, without locks on the frozen layer. Each thread also checks the frozen child's
        /// invariants every round, which walk the root's holders of the child's loser with storage of their own.
        /// </summary>
        [Fact]
        public void AFrozenChildsClaimsAreReadFromAnyThread()
        {
            const int readers = 8;
            const int vectors = 200;
            var (store, _, env) = Setup();
            var root = store.Root;
            var t0 = root.NewId();
            var t1 = root.NewId();
            var heads = Enumerable.Range(0, vectors).Select(_ => root.NewId()).ToArray();
            var ids = heads.Select(h => Intern(root, h, t1)).ToArray();
            root.Freeze();

            var child = root.CreateChild();
            child.Union(t0, t1);
            child.Rebuild();
            child.Freeze();

            using var start = new Barrier(readers);
            var failures = new List<string>();
            var tasks = Enumerable.Range(0, readers).Select(_ => Task.Factory.StartNew(() =>
            {
                using var grandchild = child.CreateChild();
                start.SignalAndWait();
                for (var round = 0; round < 20; round++)
                {
                    try
                    {
                        child.CheckInvariants();
                    }
                    catch (Exception e)
                    {
                        lock (failures)
                            failures.Add($"the frozen child's invariants: {e.Message}");
                    }

                    foreach (var context in new[] { child, grandchild })
                    {
                        var view = ContainerView.Of(context.Database);
                        for (var j = 0; j < vectors; j++)
                        {
                            var expected = new VecContainer(heads[j], t0);
                            if (view.TryGetContainer(env, ids[j], out var seen) == false || seen.Equals(expected) == false)
                                lock (failures)
                                    failures.Add($"id {ids[j]} reads {(view.TryGetContainer(env, ids[j], out var again) ? again.ToString() : "nothing")}");

                            if (view.TryGetId(env, expected, out var id) == false || id != ids[j])
                                lock (failures)
                                    failures.Add($"[{heads[j]}, {t0}] reads id {id}");
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning)).ToArray();
            Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(60)));
            Assert.Empty(failures);
        }

    }

}
