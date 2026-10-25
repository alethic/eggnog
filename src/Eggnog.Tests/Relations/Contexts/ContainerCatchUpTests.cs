using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child's catch-up with the containers of classes its writable ancestors gain after it exists
    /// (<see cref="ContainerLayers.ScanAdds"/>, <see cref="ContainerLayers.CatchUp"/>), storage part. A container an
    /// ancestor gains that equals one the child holds under another id merges into the smaller id, in either order of
    /// the two ids, under a writable root and under a writable parent of a frozen root, over a layer small enough for a
    /// full rebuild and one large enough for egglog's incremental one; a container the root interns naming the child's
    /// loser is claimed, and its copy meets the child's own; content an ancestor renames onto an id the child
    /// claimed is adopted, a map's included, while the child's copy stands for a content the ancestor rebuilds in place
    /// there, beside an adopted one too, and for a map's content the ancestor merges onto the id from another id the
    /// child claimed, whether the child merged the two before or not; a claim after such a merge copies the ancestor's
    /// version of the map too, a departure pinned; content renamed onto an id local to a writable parent is adopted; an
    /// entry under an id a level in between shadows is left to that level, while content renamed onto an id the
    /// grandchild claimed from an id only the level in between claimed is adopted, a vector's and a set's, whose later
    /// congruence it then decides; two contents under one id that share their content hash are both adopted; and the
    /// entries the pre-loop scan keeps survive a rebuild that runs out of epochs, are handled by a freeze and dropped
    /// by a dispose.
    /// </summary>
    /// <remarks>
    /// egglog has no writable parent under a live <c>(push)</c>, so the expected views come from a fresh root that
    /// replays the same commands in the same order, the child's union before the ancestor's write where the child made
    /// it first; ids line up with the replay's except where a test says how they correspond. The pinned departure,
    /// <see cref="AClaimAfterTheRootMergedAClaimedIdIntoItCopiesTheRootsHistoryToo"/>, checks the replay too, and
    /// how the child differs from it.
    /// </remarks>
    public class ContainerCatchUpTests
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
        /// A serial store with maps keyed by e-class ids registered at the root, their values not rebuilt, and the
        /// root's environment of maps.
        /// </summary>
        static (Store Store, ContainerEnv<Relations.MapContainer> Env) MapSetup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var values = store.Root.Database.Containers;
            values.RegisterType<Relations.MapContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter), holdsIds: true);
            return (store, values.GetEnvironment<Relations.MapContainer>());
        }

        /// <summary>
        /// Interns a vector of <paramref name="elements"/> at the root.
        /// </summary>
        static uint Intern(Context root, params uint[] elements) =>
            root.Database.WithExecutionState(state => state.Containers.GetOrInsert(new VecContainer(elements), state));

        /// <summary>
        /// A map keyed by classes whose values are not rebuilt.
        /// </summary>
        static Relations.MapContainer Map(uint[] keys, uint[] values) => new(true, false, keys, values);

        /// <summary>
        /// Interns <paramref name="map"/> at the root.
        /// </summary>
        static uint InternMap(Context root, Relations.MapContainer map) =>
            root.Database.WithExecutionState(state => state.Containers.GetOrInsert(map, state));

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
        /// Gets the entries the context's own layer of the type <paramref name="env"/> holds under
        /// <paramref name="id"/>; empty when it has no layer of the type.
        /// </summary>
        static List<TContainer> OwnUnder<TContainer>(Context context, ContainerEnv<TContainer> env, uint id)
            where TContainer : struct, IContainerValue<TContainer>
        {
            var entries = new List<ContainerEntry<TContainer>>();
            if (context.ContainerLayers!.LayerAt(env.TypeId) is ContainerLayer<TContainer> layer)
                layer.Own.CollectById(id, entries, locked: true);

            return entries.Select(entry => entry.Container).ToList();
        }

        /// <summary>
        /// Gets what <paramref name="context"/> sees of the containers of the type <paramref name="root"/> holds, read
        /// level by level, each entry as <c>id:content</c>, sorted: for a fresh root, its whole environment.
        /// </summary>
        static List<string> Seen<TContainer>(Context context, ContainerEnv<TContainer> root)
            where TContainer : struct, IContainerValue<TContainer> =>
            ContainerPassDifferentialTests.VisibleEntries(context, root).Select(entry => $"{entry.Id}:{entry.Container}").Order(StringComparer.Ordinal).ToList();

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
            foreach (var entry in entries)
                Assert.Equal(1, own.CollectById(entry.Id, new List<ContainerEntry<VecContainer>>(), locked: true));
        }

        /// <summary>
        /// Advances the store's clock so that only <paramref name="left"/> epochs remain.
        /// </summary>
        static void LeaveEpochs(Store store, long left)
        {
            store.Counters.IncrementBy(store.ClockCounter, Value.StaleRep - left - store.Counters.Read(store.ClockCounter));
        }

        /// <summary>
        /// The collisions of <see cref="CollisionsSettleIntoTheSmallerId"/>: per pair, <c>k &lt; w</c> and <c>m</c>,
        /// the root's <c>X = [m]</c> and <c>L = [w]</c>, <c>X</c> the smaller id when <paramref name="ancestorWins"/>,
        /// and the rows <c>f(X) = 1</c>, <c>f(L) = 2</c>; with <paramref name="large"/>, 1100 vectors <c>[n, t1]</c>
        /// after them. Two stores built with the same arguments hold the same ids.
        /// </summary>
        static (Store Store, TableId F, ContainerEnv<VecContainer> Env, uint T0, uint T1, (uint K, uint W, uint M, uint X, uint L)[] Pairs) CollisionSetup(bool ancestorWins, bool large)
        {
            const int pairs = 12;
            var (store, f, env) = Setup();
            var root = store.Root;
            var t0 = root.NewId();
            var t1 = root.NewId();
            var result = new (uint K, uint W, uint M, uint X, uint L)[pairs];
            for (var i = 0; i < pairs; i++)
            {
                var k = root.NewId();
                var w = root.NewId();
                var m = root.NewId();
                uint x, l;
                if (ancestorWins)
                {
                    x = Intern(root, m);
                    l = Intern(root, w);
                }
                else
                {
                    l = Intern(root, w);
                    x = Intern(root, m);
                }

                Insert(root, f, x, 1, 0);
                Insert(root, f, l, 2, 0);
                result[i] = (k, w, m, x, l);
            }

            for (var n = 0; n < (large ? 1100 : 0); n++)
                Intern(root, root.NewId(), t1);

            return (store, f, env, t0, t1, result);
        }

        /// <summary>
        /// Collisions (ii): the child claims each <c>L = [w]</c> when its union displaces <c>w</c> into <c>k</c>, and
        /// holds the copy <c>[k]</c>; then a writable ancestor, the root or a writable parent of a frozen root, unions
        /// <c>k</c> and <c>m</c>, so that its <c>X = [m]</c> becomes <c>[k]</c> (the root rebuilds it in place, the
        /// parent claims and rebuilds it), which its container log records. The child's view would hold <c>[k]</c>
        /// under <c>L</c> and under <c>X</c>; its catch-up settles each pair at once: the smaller id wins, the layer's
        /// entry is taken out when it is <c>X</c>, and <c>X</c> is claimed into <c>L</c> when it is <c>L</c>, and the
        /// rows of <c>f</c> meet under the winner. With <paramref name="large"/>, the child first claims 1100 more
        /// containers, so its pass after the catch-up rebuilds incrementally, revisiting only what the pairs reach;
        /// without, it rebuilds its layer in full. Either way each id of the child's layer holds one content, the
        /// invariants hold, and the child agrees with a fresh root replaying the child's unions and then the
        /// ancestor's, ids included; the ancestor keeps its own.
        /// </summary>
        /// <param name="ancestorWins">Whether the ancestor's <c>X</c> is the smaller id.</param>
        /// <param name="intermediate">Whether the ancestor is a writable parent of a frozen root, rather than a writable
        /// root.</param>
        /// <param name="large">Whether the child's layer holds more than 1000 containers.</param>
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void CollisionsSettleIntoTheSmallerId(bool ancestorWins, bool intermediate, bool large)
        {
            var (store, f, env, t0, t1, pairs) = CollisionSetup(ancestorWins, large);
            var root = store.Root;
            Context ancestor;
            Context child;
            if (intermediate)
            {
                root.Freeze();
                ancestor = root.CreateChild();
                child = ancestor.CreateChild();
            }
            else
            {
                ancestor = root;
                child = root.CreateChild();
            }

            if (large)
            {
                child.Union(t0, t1);
                child.Rebuild();
            }

            foreach (var (k, w, _, _, _) in pairs)
                child.Union(k, w);

            child.Rebuild();
            foreach (var (k, _, _, _, l) in pairs)
                Assert.Equal(new VecContainer(k), ById(child, env, l));

            foreach (var (k, _, m, _, _) in pairs)
                ancestor.Union(k, m);

            ancestor.Rebuild();
            foreach (var (k, _, _, x, _) in pairs)
                Assert.Equal(new VecContainer(k), ById(ancestor, env, x));

            VecContainer.TryRebuildCalls = 0;
            Assert.True(child.Rebuild());
            var rebuildCalls = VecContainer.TryRebuildCalls;
            child.CheckInvariants();
#if DEBUG
            // A DEBUG build checks the invariants at the end of every rebuild, which rebuilds every container of the
            // layer once more to check it is canonical.
            rebuildCalls -= VecContainer.TryRebuildCalls - rebuildCalls;
#endif
            var own = ((ContainerLayer<VecContainer>)child.ContainerLayers!.LayerAt(env.TypeId)!).Own;
            if (large)
                Assert.True(rebuildCalls < 300, $"the incremental rebuild over {own.Count} containers revisited {rebuildCalls}");
            else
                Assert.True(rebuildCalls >= pairs.Length, $"the full rebuild revisited {rebuildCalls} containers");

            Assert.False(child.IsFaulted);
            Assert.False(child.ContainerLayers.HasPending);
            foreach (var (k, w, _, x, l) in pairs)
            {
                var (winner, loser) = x < l ? (x, l) : (l, x);
                Assert.Equal(winner, child.Find(loser));
                Assert.Equal(new VecContainer(k), ById(child, env, winner));
                Assert.Null(ById(child, env, loser));
                Assert.Equal(winner, ByContent(child, env, k));
                Assert.Null(ByContent(child, env, w));
                Assert.Equal(2u, Row(child, f, winner)![1]);
                Assert.Null(Row(child, f, loser));
            }

            AssertOneContentPerId(child, env);

            // The ancestor holds what it held: the child's unions and merges are its own.
            foreach (var (k, w, _, x, l) in pairs)
            {
                Assert.False(ancestor.AreEqual(x, l));
                Assert.Equal(new VecContainer(w), ById(ancestor, env, l));
                Assert.Equal(new VecContainer(k), ById(ancestor, env, x));
            }

            // A fresh root replaying the child's unions and then the ancestor's.
            var (fresh, freshF, freshEnv, ft0, ft1, freshPairs) = CollisionSetup(ancestorWins, large);
            var replay = fresh.Root;
            if (large)
            {
                replay.Union(ft0, ft1);
                replay.Rebuild();
            }

            foreach (var (k, w, _, _, _) in freshPairs)
                replay.Union(k, w);

            replay.Rebuild();
            foreach (var (k, _, m, _, _) in freshPairs)
                replay.Union(k, m);

            replay.Rebuild();
            for (var i = 0; i < pairs.Length; i++)
            {
                var (k, _, _, x, l) = freshPairs[i];
                Assert.Equal(pairs[i], freshPairs[i]);
                var winner = Math.Min(x, l);
                Assert.Equal(winner, replay.Find(Math.Max(x, l)));
                Assert.True(freshEnv.TryGetId(new VecContainer(k), out var id));
                Assert.Equal(winner, id);
                Assert.Equal(2u, Row(replay, freshF, winner)![1]);
            }

            child.Dispose();
            if (intermediate)
            {
                ancestor.CheckInvariants();
                ancestor.Dispose();
            }
        }

        /// <summary>
        /// A content an ancestor reinserts under an id the child shadows, merging with a claimed copy: the root holds
        /// <c>v = [w]</c>, which the child claims when it displaces w into k, holding <c>[k]</c>. The root then interns
        /// <c>[x]</c>, x being another loser of the child into k, under a new u: the child's catch-up claims u, its
        /// element not canonical in the child, and the copy rebuilt is <c>[k]</c>, which meets the child's own under the
        /// smaller v. A fresh root replaying the unions and the intern returns v for <c>[x]</c>, canonical there, and
        /// mints no u; the child holds <c>[k]</c> under v alone, u merged into it.
        /// </summary>
        [Fact]
        public void ARootInternAfterAChildsDisplacementMeetsItsClaimedCopy()
        {
            var (store, _, env) = Setup();
            var root = store.Root;
            var k = root.NewId();
            var w = root.NewId();
            var x = root.NewId();
            var v = Intern(root, w);
            var child = root.CreateChild();
            child.Union(k, w);
            child.Union(k, x);
            child.Rebuild();
            Assert.Equal(new VecContainer(k), ById(child, env, v));

            var u = Intern(root, x);
            Assert.True(u > v);
            Assert.True(child.Rebuild());
            Assert.Equal(v, child.Find(u));
            Assert.Equal(v, ByContent(child, env, k));
            Assert.Null(ById(child, env, u));
            Assert.Null(ByContent(child, env, x));
            Assert.Equal([new VecContainer(k)], OwnUnder(child, env, v));
            AssertOneContentPerId(child, env);
            child.CheckInvariants();

            var (fresh, _, freshEnv) = Setup();
            var replay = fresh.Root;
            var fk = replay.NewId();
            var fw = replay.NewId();
            var fx = replay.NewId();
            var fv = Intern(replay, fw);
            replay.Union(fk, fw);
            replay.Union(fk, fx);
            replay.Rebuild();
            Assert.Equal(fv, Intern(replay, replay.Find(fx)));
            Assert.True(freshEnv.TryGetContainer(fv, out var held));
            Assert.Equal(new VecContainer(fk), held);
        }

        /// <summary>
        /// A root rename onto an id the child claimed, for a map keyed by classes: the root holds <c>y = {a: 1, b:
        /// 2}</c> and <c>z = {c: 3}</c>, <c>y</c> the smaller; the child's union of a and b claims y, whose copy
        /// becomes <c>{a: 2}</c>; then the root unions y and z, so its rebuild renames <c>{c: 3}</c> onto y. The
        /// displacement of z the child imports is y in the parent, so content arrived under y, and the child adopts
        /// it: its layer holds <c>{a: 2}</c> and <c>{c: 3}</c> under y, and z is merged into y, as a fresh root
        /// replaying the key union and then the root's holds them.
        /// </summary>
        [Fact]
        public void AMapTheRootRenamesOntoAClaimedIdIsAdopted()
        {
            var (store, env) = MapSetup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var y = InternMap(root, Map([a, b], [1, 2]));
            var z = InternMap(root, Map([c], [3]));
            var child = root.CreateChild();
            child.Union(a, b);
            child.Rebuild();
            Assert.Equal([Map([a], [2])], OwnUnder(child, env, y));

            root.Union(y, z);
            root.Rebuild();
            Assert.True(env.TryGetId(Map([c], [3]), out var renamed));
            Assert.Equal(y, renamed);

            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.Equal(y, child.Find(z));
            var view = ContainerView.Of(child.Database);
            Assert.True(view.TryGetId(env, Map([a], [2]), out var ofCopy));
            Assert.Equal(y, ofCopy);
            Assert.True(view.TryGetId(env, Map([c], [3]), out var ofAdopted));
            Assert.Equal(y, ofAdopted);
            Assert.Equal(2, OwnUnder(child, env, y).Count);
            child.CheckInvariants();

            var (fresh, freshEnv) = MapSetup();
            var replay = fresh.Root;
            var fa = replay.NewId();
            var fb = replay.NewId();
            var fc = replay.NewId();
            var fy = InternMap(replay, Map([fa, fb], [1, 2]));
            var fz = InternMap(replay, Map([fc], [3]));
            replay.Union(fa, fb);
            replay.Rebuild();
            replay.Union(fy, fz);
            replay.Rebuild();
            Assert.True(freshEnv.TryGetId(Map([fa], [2]), out var freshCopy));
            Assert.Equal(fy, freshCopy);
            Assert.True(freshEnv.TryGetId(Map([fc], [3]), out var freshAdopted));
            Assert.Equal(fy, freshAdopted);
            Assert.Equal((a, b, c, y, z), (fa, fb, fc, fy, fz));
        }

        /// <summary>
        /// A claimed map that the root rebuilds in place on an id that holds two contents, one of them adopted: the
        /// root holds <c>x = {a: 1, b: 2, d: 4}</c> and <c>z = {c: 3}</c>; the child's union of a and d claims x, whose
        /// copy becomes <c>{a: 4, b: 2}</c>; the root's union of x and z renames <c>{c: 3}</c> onto x, which the child
        /// adopts; then the root's union of a and b rebuilds its first content in place to <c>{a: 2, d: 4}</c>, which
        /// rebuilt against the child's union-find is <c>{a: 4}</c>. No displacement the child imports then is x in the
        /// parent, so that entry is the content the child copied, which its copy stands for: the child adopts nothing,
        /// its copy becomes <c>{a: 2}</c>, and x holds <c>{a: 2}</c> and <c>{c: 3}</c>, as a fresh root replaying the
        /// three unions in order holds.
        /// </summary>
        [Fact]
        public void AClaimedMapRebuiltInPlaceBesideAnAdoptedContentKeepsTheChildsCopy()
        {
            var (store, env) = MapSetup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var d = root.NewId();
            var x = InternMap(root, Map([a, b, d], [1, 2, 4]));
            var z = InternMap(root, Map([c], [3]));
            var child = root.CreateChild();
            child.Union(a, d);
            child.Rebuild();
            Assert.Equal([Map([a, b], [4, 2])], OwnUnder(child, env, x));

            root.Union(x, z);
            root.Rebuild();
            child.Rebuild();
            Assert.Equal(2, OwnUnder(child, env, x).Count);

            root.Union(a, b);
            root.Rebuild();
            var under = new List<ContainerEntry<Relations.MapContainer>>();
            Assert.Equal(2, env.CollectById(x, under, locked: true));
            Assert.Contains(under, entry => entry.Container.Equals(Map([a, d], [2, 4])));

            child.ContainerLayers!.ScanAdds();
            Assert.True(child.ContainerLayers.HasPending);
            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            var held = OwnUnder(child, env, x);
            Assert.Equal(2, held.Count);
            Assert.Contains(Map([a], [2]), held);
            Assert.Contains(Map([c], [3]), held);
            Assert.False(ContainerView.Of(child.Database).TryGetId(env, Map([a], [4]), out _));
            child.CheckInvariants();

            var (fresh, freshEnv) = MapSetup();
            var replay = fresh.Root;
            var fa = replay.NewId();
            var fb = replay.NewId();
            var fc = replay.NewId();
            var fd = replay.NewId();
            var fx = InternMap(replay, Map([fa, fb, fd], [1, 2, 4]));
            var fz = InternMap(replay, Map([fc], [3]));
            replay.Union(fa, fd);
            replay.Rebuild();
            replay.Union(fx, fz);
            replay.Rebuild();
            replay.Union(fa, fb);
            replay.Rebuild();
            var replayed = new List<ContainerEntry<Relations.MapContainer>>();
            Assert.Equal(2, freshEnv.CollectById(fx, replayed, locked: true));
            Assert.Contains(replayed, entry => entry.Container.Equals(Map([fa], [2])));
            Assert.Contains(replayed, entry => entry.Container.Equals(Map([fc], [3])));
            Assert.Equal((a, b, c, d, x, z), (fa, fb, fc, fd, fx, fz));
        }

        /// <summary>
        /// A map the root merges onto an id the child claimed, from an id the child claimed too: the root holds
        /// <c>x = {d: 4, e: 5}</c> and <c>y = {a: 1, b: 2, c: 3}</c>, x the smaller; the child's unions of a with c and
        /// of d with e claim both, its copies becoming <c>{d: 5}</c> and <c>{a: 3, b: 2}</c>; then the root unions a
        /// with b, and x with y, so its rebuild moves its own version of y's content, <c>{a: 2, c: 3}</c>, onto x. The
        /// displacement of y the child imports is x in the parent, but the child shadows y: its own copy of y, rebuilt
        /// to <c>{a: 2}</c>, follows the union onto x, and the root's version, which rebuilt against the child's
        /// union-find would be <c>{a: 3}</c>, a content the replay never holds, is not adopted. The child holds
        /// <c>{a: 2}</c> and <c>{d: 5}</c> under x, as a fresh root replaying the child's unions and then the root's
        /// holds them, whether the root's two unions come in one rebuild or in two with the child's rebuild in between.
        /// </summary>
        /// <param name="oneRebuild">Whether the root rebuilds once, after both of its unions.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AMapMergedOntoAClaimedIdFromAnIdTheChildClaimedKeepsTheChildsCopy(bool oneRebuild)
        {
            var (store, env) = MapSetup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var d = root.NewId();
            var e = root.NewId();
            var x = InternMap(root, Map([d, e], [4, 5]));
            var y = InternMap(root, Map([a, b, c], [1, 2, 3]));
            var child = root.CreateChild();
            child.Union(a, c);
            child.Union(d, e);
            child.Rebuild();
            Assert.Equal([Map([d], [5])], OwnUnder(child, env, x));
            Assert.Equal([Map([a, b], [3, 2])], OwnUnder(child, env, y));

            root.Union(a, b);
            if (oneRebuild == false)
            {
                root.Rebuild();
                child.Rebuild();
                Assert.Equal([Map([a], [2])], OwnUnder(child, env, y));
            }

            root.Union(x, y);
            root.Rebuild();
            var under = new List<ContainerEntry<Relations.MapContainer>>();
            Assert.Equal(2, env.CollectById(x, under, locked: true));
            Assert.Contains(under, entry => entry.Container.Equals(Map([a, c], [2, 3])));

            child.ContainerLayers!.ScanAdds();
            Assert.True(child.ContainerLayers.HasPending);
            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.Equal(x, child.Find(y));
            var held = OwnUnder(child, env, x);
            Assert.Equal(2, held.Count);
            Assert.Contains(Map([a], [2]), held);
            Assert.Contains(Map([d], [5]), held);
            Assert.Empty(OwnUnder(child, env, y));
            Assert.False(ContainerView.Of(child.Database).TryGetId(env, Map([a], [3]), out _));
            child.CheckInvariants();

            var (fresh, freshEnv) = MapSetup();
            var replay = fresh.Root;
            var fa = replay.NewId();
            var fb = replay.NewId();
            var fc = replay.NewId();
            var fd = replay.NewId();
            var fe = replay.NewId();
            var fx = InternMap(replay, Map([fd, fe], [4, 5]));
            var fy = InternMap(replay, Map([fa, fb, fc], [1, 2, 3]));
            replay.Union(fa, fc);
            replay.Union(fd, fe);
            replay.Rebuild();
            replay.Union(fa, fb);
            if (oneRebuild == false)
                replay.Rebuild();

            replay.Union(fx, fy);
            replay.Rebuild();
            var replayed = new List<ContainerEntry<Relations.MapContainer>>();
            Assert.Equal(2, freshEnv.CollectById(fx, replayed, locked: true));
            Assert.Contains(replayed, entry => entry.Container.Equals(Map([fa], [2])));
            Assert.Contains(replayed, entry => entry.Container.Equals(Map([fd], [5])));
            Assert.Equal((a, b, c, d, e, x, y), (fa, fb, fc, fd, fe, fx, fy));
        }

        /// <summary>
        /// A map the root merges into an id the child claimed, after the child merged the two itself: the root holds
        /// <c>x = {a: 1, b: 2, d: 4}</c> and <c>w = {b: 2, d: 4}</c>, x the smaller; the child's union of a and d
        /// claims both, whose copies both become <c>{a: 4, b: 2}</c>, so it merges w into x. Then the root's union of
        /// a and b rebuilds both of its own to <c>{a: 2, d: 4}</c> and merges w into x there as well. The displacement
        /// of w the child imports is x in the parent, but the child shadows w and holds its content under x already:
        /// nothing arrived under x, so the child adopts nothing, and its copy, rebuilt to <c>{a: 2}</c>, is alone
        /// under x, as a fresh root replaying the child's union and then the root's holds it; the root's content
        /// rebuilt against the child's union-find, <c>{a: 4}</c>, is not in the child's view.
        /// </summary>
        [Fact]
        public void AMapTheRootMergesAsTheChildDidKeepsTheChildsCopyAlone()
        {
            var (store, env) = MapSetup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var d = root.NewId();
            var x = InternMap(root, Map([a, b, d], [1, 2, 4]));
            var w = InternMap(root, Map([b, d], [2, 4]));
            var child = root.CreateChild();
            child.Union(a, d);
            child.Rebuild();
            Assert.Equal(x, child.Find(w));
            Assert.Equal([Map([a, b], [4, 2])], OwnUnder(child, env, x));

            root.Union(a, b);
            root.Rebuild();
            Assert.Equal(x, root.Find(w));
            var under = new List<ContainerEntry<Relations.MapContainer>>();
            Assert.Equal(1, env.CollectById(x, under, locked: true));
            Assert.Equal(Map([a, d], [2, 4]), under[0].Container);

            child.ContainerLayers!.ScanAdds();
            Assert.True(child.ContainerLayers.HasPending);
            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.Equal([Map([a], [2])], OwnUnder(child, env, x));
            Assert.Empty(OwnUnder(child, env, w));
            Assert.False(ContainerView.Of(child.Database).TryGetId(env, Map([a], [4]), out _));
            child.CheckInvariants();

            var (fresh, freshEnv) = MapSetup();
            var replay = fresh.Root;
            var fa = replay.NewId();
            var fb = replay.NewId();
            var fd = replay.NewId();
            var fx = InternMap(replay, Map([fa, fb, fd], [1, 2, 4]));
            var fw = InternMap(replay, Map([fb, fd], [2, 4]));
            replay.Union(fa, fd);
            replay.Rebuild();
            replay.Union(fa, fb);
            replay.Rebuild();
            var replayed = new List<ContainerEntry<Relations.MapContainer>>();
            Assert.Equal(1, freshEnv.CollectById(fx, replayed, locked: true));
            Assert.Equal(Map([fa], [2]), replayed[0].Container);
            Assert.Equal(fx, replay.Find(fw));
            Assert.Equal((a, b, d, x, w), (fa, fb, fd, fx, fw));
        }

        /// <summary>
        /// The departure for maps of a claim after an ancestor's merge (decisions row 9), pinned: the root holds
        /// <c>x = {d: 4}</c> and <c>y = {a: 1, b: 2, c: 3}</c>, x the smaller; the child's union of a and c claims y
        /// alone, its copy becoming <c>{a: 3, b: 2}</c>, and <c>{a: 2}</c> after the root's union of a and b. The root
        /// then unions x and y, moving its own version of y's content, <c>{a: 2, c: 3}</c>, onto x. The child's
        /// catch-up claims x, since that content holds c, the child's loser, and the claim copies both of the root's
        /// contents under x, while the child's own copy of y follows the union onto x: x holds <c>{a: 2}</c>, the
        /// child's history of y, <c>{a: 3}</c>, the root's rebuilt against the child's union-find, and <c>{d: 4}</c>.
        /// A fresh root replaying the unions in order holds only <c>{a: 2}</c> and <c>{d: 4}</c> under x; for a
        /// vector the two histories rebuild to one content.
        /// </summary>
        [Fact]
        public void AClaimAfterTheRootMergedAClaimedIdIntoItCopiesTheRootsHistoryToo()
        {
            var (store, env) = MapSetup();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var d = root.NewId();
            var x = InternMap(root, Map([d], [4]));
            var y = InternMap(root, Map([a, b, c], [1, 2, 3]));
            var child = root.CreateChild();
            child.Union(a, c);
            child.Rebuild();
            root.Union(a, b);
            root.Rebuild();
            child.Rebuild();
            Assert.Equal([Map([a], [2])], OwnUnder(child, env, y));
            Assert.Empty(OwnUnder(child, env, x));

            root.Union(x, y);
            root.Rebuild();
            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.Equal(x, child.Find(y));
            var held = OwnUnder(child, env, x);
            Assert.Equal(3, held.Count);
            Assert.Contains(Map([a], [2]), held);
            Assert.Contains(Map([a], [3]), held);
            Assert.Contains(Map([d], [4]), held);
            Assert.True(ContainerView.Of(child.Database).TryGetId(env, Map([a], [3]), out var extra));
            Assert.Equal(x, extra);
            child.CheckInvariants();

            var (fresh, freshEnv) = MapSetup();
            var replay = fresh.Root;
            var fa = replay.NewId();
            var fb = replay.NewId();
            var fc = replay.NewId();
            var fd = replay.NewId();
            var fx = InternMap(replay, Map([fd], [4]));
            var fy = InternMap(replay, Map([fa, fb, fc], [1, 2, 3]));
            replay.Union(fa, fc);
            replay.Rebuild();
            replay.Union(fa, fb);
            replay.Rebuild();
            replay.Union(fx, fy);
            replay.Rebuild();
            var replayed = new List<ContainerEntry<Relations.MapContainer>>();
            Assert.Equal(2, freshEnv.CollectById(fx, replayed, locked: true));
            Assert.Contains(replayed, entry => entry.Container.Equals(Map([fa], [2])));
            Assert.Contains(replayed, entry => entry.Container.Equals(Map([fd], [4])));
            Assert.Equal((a, b, c, d, x, y), (fa, fb, fc, fd, fx, fy));
        }

        /// <summary>
        /// Content renamed onto an id local to a writable parent, which only raw engine unions of container ids with
        /// classes make: under a writable root, the parent mints y, then the root interns <c>x2 = [c]</c> and
        /// <c>x = [k]</c>, so y is the smallest. The grandchild's union of x2 and y claims x2 and renames its copy onto
        /// y, which it claims, empty, from the parent. Then the parent's union of x and y claims x and renames its copy
        /// onto y, local to it, which its container log records. The displacement of x the grandchild imports is y in
        /// the parent, so content arrived under y, and the grandchild adopts it: it holds <c>[c]</c> and <c>[k]</c>
        /// under y. A fresh root replaying the chain, its ids minted in the same order, holds both under its own id for
        /// y; the parent's id block makes the ids differ.
        /// </summary>
        [Fact]
        public void ContentRenamedOntoAnIdLocalToAWritableParentIsAdopted()
        {
            var (store, _, env) = Setup();
            var root = store.Root;
            var c = root.NewId();
            var k = root.NewId();
            var parent = root.CreateChild();
            var y = parent.NewId();
            var x2 = Intern(root, c);
            var x = Intern(root, k);
            Assert.True(y < x2 && x2 < x);

            var grandchild = parent.CreateChild();
            grandchild.Union(x2, y);
            grandchild.Rebuild();
            Assert.Equal([new VecContainer(c)], OwnUnder(grandchild, env, y));
            Assert.True(grandchild.ContainerLayers!.LayerAt(env.TypeId)!.Shadowed.Contains(y));

            parent.Union(x, y);
            parent.Rebuild();
            Assert.Equal([new VecContainer(k)], OwnUnder(parent, env, y));

            Assert.True(grandchild.Rebuild());
            Assert.False(grandchild.IsFaulted);
            Assert.Equal(y, grandchild.Find(x2));
            Assert.Equal(y, grandchild.Find(x));
            Assert.Equal(y, ByContent(grandchild, env, c));
            Assert.Equal(y, ByContent(grandchild, env, k));
            var held = OwnUnder(grandchild, env, y);
            Assert.Equal(2, held.Count);
            Assert.Contains(new VecContainer(c), held);
            Assert.Contains(new VecContainer(k), held);
            grandchild.CheckInvariants();
            parent.CheckInvariants();

            var (fresh, _, freshEnv) = Setup();
            var replay = fresh.Root;
            var fc = replay.NewId();
            var fk = replay.NewId();
            var fy = replay.NewId();
            var fx2 = Intern(replay, fc);
            var fx = Intern(replay, fk);
            replay.Union(fx2, fy);
            replay.Rebuild();
            replay.Union(fx, fy);
            replay.Rebuild();
            Assert.True(freshEnv.TryGetId(new VecContainer(fc), out var ofC));
            Assert.Equal(fy, ofC);
            Assert.True(freshEnv.TryGetId(new VecContainer(fk), out var ofK));
            Assert.Equal(fy, ofK);
            Assert.Equal(fy, replay.Find(fx2));
            Assert.Equal(fy, replay.Find(fx));
        }

        /// <summary>
        /// Hidden-above entries with a writable intermediate level: under a writable root, the parent claims y, the
        /// root's <c>[w]</c>, when its union displaces w into k, and holds <c>[k]</c>; the child below it has a union of
        /// its own, of other classes. The root then unions y and z, renaming its <c>[a]</c> onto y, and the parent's
        /// catch-up adopts it, which the parent's log records. The child classifies the root's entry as hidden above,
        /// the parent shadowing y, and leaves it to the parent; the parent's entry it sees as it is. Its pre-loop scan
        /// keeps neither, it claims nothing, and it sees the parent's two contents under y, as a fresh root replaying the
        /// chain holds them.
        /// </summary>
        [Fact]
        public void AnEntryALevelInBetweenShadowsIsLeftToThatLevel()
        {
            var (store, _, env) = Setup();
            var root = store.Root;
            var k = root.NewId();
            var w = root.NewId();
            var a = root.NewId();
            var q = root.NewId();
            var r = root.NewId();
            var y = Intern(root, w);
            var z = Intern(root, a);
            var parent = root.CreateChild();
            var child = parent.CreateChild();
            parent.Union(k, w);
            parent.Rebuild();
            child.Union(q, r);
            child.Rebuild();
            Assert.Equal([new VecContainer(k)], OwnUnder(parent, env, y));

            root.Union(y, z);
            root.Rebuild();
            parent.Rebuild();
            Assert.Equal(2, OwnUnder(parent, env, y).Count);

            var layers = child.ContainerLayers!;
            var rebuilder = child.UnionFindTable.GetRebuilder(ReadOnlySpan<ColumnId>.Empty)!;
            var entry = new ContainerLogEntry(env.TypeId, y, new VecContainer(a).ComputeHash());
            Assert.Equal(ContainerCatchUpClass.HiddenAbove, env.ClassifyAdded(layers, 0, entry, rebuilder));
            Assert.Equal(ContainerCatchUpClass.Inert, env.ClassifyAdded(layers, 1, entry, rebuilder));
            layers.ScanAdds();
            Assert.False(layers.HasPending);

            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.True(layers.IsEmpty);
            Assert.Equal(y, child.Find(z));
            Assert.Equal(y, ByContent(child, env, k));
            Assert.Equal(y, ByContent(child, env, a));
            Assert.Null(ByContent(child, env, w));
            child.CheckInvariants();
            parent.CheckInvariants();

            var (fresh, _, freshEnv) = Setup();
            var replay = fresh.Root;
            var fk = replay.NewId();
            var fw = replay.NewId();
            var fa = replay.NewId();
            var fq = replay.NewId();
            var fr = replay.NewId();
            var fy = Intern(replay, fw);
            var fz = Intern(replay, fa);
            replay.Union(fk, fw);
            replay.Union(fq, fr);
            replay.Rebuild();
            replay.Union(fy, fz);
            replay.Rebuild();
            Assert.True(freshEnv.TryGetId(new VecContainer(fk), out var ofK));
            Assert.Equal(fy, ofK);
            Assert.True(freshEnv.TryGetId(new VecContainer(fa), out var ofA));
            Assert.Equal(fy, ofA);
            Assert.Equal((y, z), (fy, fz));
        }

        /// <summary>
        /// Content renamed onto an id the grandchild claimed, from an id only the level in between claimed: the
        /// ancestor, a writable root or a writable parent of a frozen root, holds <c>x = [x2]</c> and <c>y = [y2]</c>,
        /// x the smaller. The child's union of y1 and y2 claims y, its copy <c>[y1]</c>, and the grandchild's union of
        /// x1 and x2 claims x, its copy <c>[x1]</c>, in either order. The ancestor then unions y1 and y2, so that its y
        /// holds <c>[y1]</c> too, and x and y, renaming that content onto x. The grandchild's rebuild rebuilds the
        /// child first, whose catch-up finds the ancestor's <c>[y1]</c> under x, an id the child does not claim, equal
        /// to its copy under y, and settles the collision: x wins, so the child takes its copy out and claims nothing.
        /// The grandchild shadows x and not y: the displacement of y it imports is x in the parent, so content arrived
        /// under x, and the grandchild adopts the ancestor's <c>[y1]</c>, though the child shadows y; no level holds a
        /// copy of it for the grandchild otherwise. The grandchild sees <c>[x1]</c> and <c>[y1]</c> under x, as a
        /// fresh root replaying the unions in order holds them, whether the ancestor rebuilds after each of its unions
        /// or once.
        /// </summary>
        /// <param name="intermediate">Whether the ancestor is a writable parent of a frozen root, rather than a
        /// writable root.</param>
        /// <param name="grandchildFirst">Whether the grandchild's union comes before the child's.</param>
        /// <param name="oneRebuild">Whether the ancestor rebuilds once, after both of its unions.</param>
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void ContentRenamedOntoAClaimedIdFromAnIdOnlyALevelInBetweenClaimedIsAdopted(bool intermediate, bool grandchildFirst, bool oneRebuild)
        {
            var (store, _, env) = Setup();
            var root = store.Root;
            var x1 = root.NewId();
            var x2 = root.NewId();
            var y1 = root.NewId();
            var y2 = root.NewId();
            var x = Intern(root, x2);
            var y = Intern(root, y2);
            Context ancestor;
            if (intermediate)
            {
                root.Freeze();
                ancestor = root.CreateChild();
            }
            else
            {
                ancestor = root;
            }

            var child = ancestor.CreateChild();
            var grandchild = child.CreateChild();
            foreach (var first in new[] { grandchildFirst, grandchildFirst == false })
            {
                if (first)
                {
                    grandchild.Union(x1, x2);
                    grandchild.Rebuild();
                }
                else
                {
                    child.Union(y1, y2);
                    child.Rebuild();
                }
            }

            Assert.Equal([new VecContainer(y1)], OwnUnder(child, env, y));
            Assert.Equal([new VecContainer(x1)], OwnUnder(grandchild, env, x));

            ancestor.Union(y1, y2);
            if (oneRebuild == false)
            {
                ancestor.Rebuild();
                grandchild.Rebuild();
            }

            ancestor.Union(x, y);
            ancestor.Rebuild();
            Assert.True(grandchild.Rebuild());
            Assert.False(grandchild.IsFaulted);
            Assert.Equal(x, child.Find(y));
            Assert.Empty(OwnUnder(child, env, x));
            Assert.Empty(OwnUnder(child, env, y));
            Assert.Equal(x, ByContent(child, env, y1));
            Assert.Equal(x, grandchild.Find(y));
            Assert.Equal(x, ByContent(grandchild, env, x1));
            Assert.Equal(x, ByContent(grandchild, env, y1));
            child.CheckInvariants();
            grandchild.CheckInvariants();

            var (fresh, _, freshEnv) = Setup();
            var replay = fresh.Root;
            var fx1 = replay.NewId();
            var fx2 = replay.NewId();
            var fy1 = replay.NewId();
            var fy2 = replay.NewId();
            var fx = Intern(replay, fx2);
            var fy = Intern(replay, fy2);
            foreach (var first in new[] { grandchildFirst, grandchildFirst == false })
            {
                replay.Union(first ? fx1 : fy1, first ? fx2 : fy2);
                replay.Rebuild();
            }

            replay.Union(fy1, fy2);
            if (oneRebuild == false)
                replay.Rebuild();

            replay.Union(fx, fy);
            replay.Rebuild();
            Assert.Equal((x1, x2, y1, y2, x, y), (fx1, fx2, fy1, fy2, fx, fy));
            Assert.Equal(Seen(replay, freshEnv), Seen(grandchild, env));
            Assert.Equal([$"{x}:{new VecContainer(x1)}", $"{x}:{new VecContainer(y1)}"], Seen(grandchild, env));

            grandchild.Dispose();
            child.Dispose();
            if (intermediate)
            {
                ancestor.CheckInvariants();
                ancestor.Dispose();
            }
        }

        /// <summary>
        /// The same with sets, and a congruence it decides: the ancestor holds the classes 0 to 4 and <c>s = {2}</c>,
        /// <c>t = {3}</c> and <c>v = {4}</c>. The child's union of 0 and 3 claims t, its copy <c>{0}</c>, and the
        /// grandchild's of 1 and 2 claims s, its copy <c>{1}</c>. The ancestor's union of 0 and 3 makes its t
        /// <c>{0}</c>, and its union of s and t renames that onto s; the child drops its copy, which equals the
        /// ancestor's under s, and the grandchild adopts the ancestor's, which reaches it from no level otherwise: it
        /// sees <c>{0}</c> and <c>{1}</c> under s, and <c>{4}</c> under v. Its union of 0 and 4 then makes v
        /// <c>{0}</c>, which merges v into s, as a fresh root replaying the unions in order merges them.
        /// </summary>
        /// <param name="intermediate">Whether the ancestor is a writable parent of a frozen root, rather than a
        /// writable root.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASetRenamedOntoAClaimedIdFromAnIdOnlyALevelInBetweenClaimedMeetsALaterEqualSet(bool intermediate)
        {
            static (Store Store, ContainerEnv<SetContainer> Env, uint S, uint T, uint V) Build()
            {
                var store = new Store(ParallelHeuristics.Serial);
                var values = store.Root.Database.Containers;
                values.RegisterType<SetContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
                var root = store.Root;
                for (var i = 0; i < 5; i++)
                    root.NewId();

                uint Set(uint element) => root.Database.WithExecutionState(state => state.Containers.GetOrInsert(new SetContainer(element), state));
                return (store, values.GetEnvironment<SetContainer>(), Set(2), Set(3), Set(4));
            }

            var (store, env, s, t, v) = Build();
            var root = store.Root;
            Context ancestor;
            if (intermediate)
            {
                root.Freeze();
                ancestor = root.CreateChild();
            }
            else
            {
                ancestor = root;
            }

            var child = ancestor.CreateChild();
            var grandchild = child.CreateChild();
            var (fresh, freshEnv, fs, ft, fv) = Build();
            var replay = fresh.Root;
            Assert.Equal((s, t, v), (fs, ft, fv));
            foreach (var (actor, a, b) in new[] { (child, 0u, 3u), (grandchild, 1u, 2u), (ancestor, 0u, 3u), (ancestor, s, t) })
            {
                actor.Rebuild();
                actor.Union(a, b);
                actor.Rebuild();
                replay.Union(a, b);
                replay.Rebuild();
            }

            Assert.True(grandchild.Rebuild());
            Assert.Equal(Seen(replay, freshEnv), Seen(grandchild, env));
            Assert.Equal([$"{s}:{new SetContainer(0)}", $"{s}:{new SetContainer(1)}", $"{v}:{new SetContainer(4)}"], Seen(grandchild, env));
            Assert.True(ContainerView.Of(grandchild.Database).TryGetId(env, new SetContainer(0), out var adopted));
            Assert.Equal(s, adopted);
            child.CheckInvariants();
            grandchild.CheckInvariants();

            grandchild.Union(0, 4);
            Assert.True(grandchild.Rebuild());
            replay.Union(0, 4);
            replay.Rebuild();
            Assert.Equal(s, replay.Find(v));
            Assert.Equal(s, grandchild.Find(v));
            Assert.Equal(Seen(replay, freshEnv), Seen(grandchild, env));
            grandchild.CheckInvariants();

            grandchild.Dispose();
            child.Dispose();
            if (intermediate)
            {
                ancestor.CheckInvariants();
                ancestor.Dispose();
            }
        }

        /// <summary>
        /// Two contents under one id that share their content hash, which one Added entry names together: a function
        /// value hashes its target, its rebuild flags and its arguments, and FxHasher absorbs a leading zero word, so
        /// <c>fn#0/1[1, c]</c> (target 0, the first argument rebuilt) hashes as <c>fn#1/1[c]</c>. Under a writable root
        /// with classes 0 to 2, <c>f = fn#1/1[2]</c> and <c>g = fn#0/1[1, 0]</c>, f the smaller: the child's union of 0
        /// and 2 claims f, its copy <c>fn#1/1[0]</c>; the root's own union of 0 and 2 rebuilds its f in place to
        /// <c>fn#1/1[0]</c>; and its union of f and g renames g's content onto f, the same hash under the same id.
        /// Without <paramref name="inPlace"/>, the classes are 0 to 3, <c>f = fn#1/1[3]</c> and <c>g = fn#0/1[1,
        /// 3]</c>, the child's union of 2 and 3 claims f alone, its copy <c>fn#1/1[2]</c>, and the root only unions f
        /// and g, so its f's own content and g's share the id and the hash. Either way the child adopts g's content,
        /// beside its copy of f's, and sees both under f, as a fresh root replaying the unions in order holds them.
        /// </summary>
        /// <param name="inPlace">Whether the root rebuilds f in place before the rename.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TwoContentsUnderOneIdWithOneContentHashAreBothAdopted(bool inPlace)
        {
            (Store Store, ContainerEnv<FunctionContainer> Env, uint F, uint G) Build()
            {
                var store = new Store(ParallelHeuristics.Serial);
                var values = store.Root.Database.Containers;
                values.RegisterType<FunctionContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
                var root = store.Root;
                for (var i = 0; i < (inPlace ? 3 : 4); i++)
                    root.NewId();

                uint Fn(int target, params uint[] arguments) =>
                    root.Database.WithExecutionState(state => state.Containers.GetOrInsert(new FunctionContainer(target, 1, arguments), state));

                return inPlace
                    ? (store, values.GetEnvironment<FunctionContainer>(), Fn(1, 2), Fn(0, 1, 0))
                    : (store, values.GetEnvironment<FunctionContainer>(), Fn(1, 3), Fn(0, 1, 3));
            }

            var (store, env, f, g) = Build();
            var root = store.Root;
            var child = root.CreateChild();
            var (fresh, freshEnv, ff, fg) = Build();
            var replay = fresh.Root;
            Assert.Equal((f, g), (ff, fg));
            var gContent = inPlace ? new FunctionContainer(0, 1, [1, 0]) : new FunctionContainer(0, 1, [1, 3]);
            var copy = inPlace ? new FunctionContainer(1, 1, [0]) : new FunctionContainer(1, 1, [2]);
            Assert.Equal(gContent.ComputeHash(), (inPlace ? copy : new FunctionContainer(1, 1, [3])).ComputeHash());

            var steps = inPlace
                ? new[] { (child, 0u, 2u), (root, 0u, 2u), (root, f, g) }
                : new[] { (child, 2u, 3u), (root, f, g) };
            foreach (var (actor, a, b) in steps)
            {
                actor.Rebuild();
                actor.Union(a, b);
                actor.Rebuild();
                replay.Union(a, b);
                replay.Rebuild();
            }

            Assert.True(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.Equal(f, child.Find(g));
            var view = ContainerView.Of(child.Database);
            Assert.True(view.TryGetId(env, gContent, out var adopted));
            Assert.Equal(f, adopted);
            Assert.True(view.TryGetId(env, copy, out var held));
            Assert.Equal(f, held);
            Assert.Equal(Seen(replay, freshEnv), Seen(child, env));
            Assert.Equal(2, OwnUnder(child, env, f).Count);
            child.CheckInvariants();
            child.Dispose();
        }

        /// <summary>
        /// Builds the collision of <see cref="ContainerContextTests.AContainerTheRootInternsEqualToAClaimedCopyMergesWithIt"/>
        /// and runs the child's pre-loop scan, which keeps the root's new entry for the catch-up: the root's
        /// <c>v = [w]</c>, the child's copy <c>[k]</c> under it, and the root's later <c>u = [k]</c>.
        /// </summary>
        static (Store Store, ContainerEnv<VecContainer> Env, Context Child, uint K, uint V, uint U) PendingSetup()
        {
            var (store, _, env) = Setup();
            var root = store.Root;
            var k = root.NewId();
            var w = root.NewId();
            var v = Intern(root, w);
            var child = root.CreateChild();
            child.Union(k, w);
            child.Rebuild();
            var u = Intern(root, k);
            child.ContainerLayers!.ScanAdds();
            Assert.Equal(1, child.ContainerLayers.PendingCount);
            Assert.False(child.IsClean);
            Assert.False(child.IsViewCurrent);
            return (store, env, child, k, v, u);
        }

        /// <summary>
        /// A rebuild that runs out of epochs before its first pass keeps what the pre-loop scan kept: the child is not
        /// faulted, stays unclean, and its entry still waits; a dispose then drops it, and clears the root's log.
        /// </summary>
        [Fact]
        public void PendingEntriesSurviveARebuildThatRunsOutOfEpochs()
        {
            var (store, _, child, _, _, _) = PendingSetup();
            LeaveEpochs(store, 0);
            Assert.Throws<OverflowException>(() => child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.False(child.IsClean);
            Assert.Equal(1, child.ContainerLayers!.PendingCount);

            child.Dispose();
            Assert.False(child.ContainerLayers.HasPending);
            Assert.False(store.ContainerLog.Active);
            Assert.Equal(0, store.ContainerLog.AddedCount);
            store.Root.CheckInvariants();
        }

        /// <summary>
        /// A freeze with an entry waiting: the freeze's rebuild settles the collision, so the frozen child holds
        /// <c>[k]</c> under v alone, u merged into it, and nothing waits once it is frozen.
        /// </summary>
        [Fact]
        public void AFreezeHandlesThePendingEntries()
        {
            var (store, env, child, k, v, u) = PendingSetup();
            store.Root.Freeze();
            child.Freeze();
            Assert.True(child.IsFrozen);
            Assert.False(child.ContainerLayers!.HasPending);
            Assert.Equal(v, child.Find(u));
            Assert.Equal(v, ByContent(child, env, k));
            Assert.Null(ById(child, env, u));
            child.CheckInvariants();
            child.Dispose();
        }

        /// <summary>
        /// A dispose with an entry waiting drops it: nothing else changes, the root's log is cleared with its last
        /// descendant, and the root still holds both vectors.
        /// </summary>
        [Fact]
        public void ADisposeDropsThePendingEntries()
        {
            var (store, env, child, k, v, u) = PendingSetup();
            child.Dispose();
            Assert.False(child.ContainerLayers!.HasPending);
            Assert.False(store.ContainerLog.Active);
            Assert.True(env.TryGetContainer(u, out var interned));
            Assert.Equal(new VecContainer(k), interned);
            Assert.False(store.Root.AreEqual(u, v));
            store.Root.CheckInvariants();
        }

    }

}
