using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ContainerValues"/>: type registration, interning and lookups by type, and
    /// <see cref="ContainerValues.RebuildAll"/> with its incremental heuristic, its parallel arm and the dirty-id
    /// closure (egglog's core-relations/src/containers/mod.rs:124-257).
    /// </summary>
    public class ContainerValuesTests
    {

        static (Database Db, TableId Uf, CounterId Ids) Setup(ParallelHeuristics? parallelism = null)
        {
            var db = new Database(parallelism ?? ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var ids = db.AddCounter();
            return (db, uf, ids);
        }

        static uint Find(Database db, TableId uf, uint id) => ((DisplacedTable)db.GetTable(uf)).FindNaive(id);

        static uint NewId(Database db, CounterId ids) => (uint)db.IncrementCounter(ids);

        static void Union(Database db, TableId uf, uint a, uint b)
        {
            Stage(db, uf, R(a, b, 1));
            db.MergeAll();
        }

        /// <summary>
        /// Rebuilds the containers in a state at <paramref name="timestamp"/>, by default 2, after the unions the tests
        /// stage at 1: the union-find requires nondecreasing timestamps, and merges without a clock stamp their unions
        /// with the state's.
        /// </summary>
        static ContainerRebuildSummary RebuildAll(Database db, TableId uf, uint timestamp = 2)
        {
            return db.WithExecutionState(es => db.Containers.RebuildAll(uf, db.GetTable(uf), es), timestamp);
        }

        [Fact]
        public void RegisterTypeAssignsDenseIdsAndKeepsTheFirstRegistration()
        {
            var values = new ContainerValues(ParallelHeuristics.Serial);
            var first = new RecordingContainerMerge();
            var vecId = values.RegisterType<VecContainer>(new CounterId(0), first);
            var setId = values.RegisterType<SetContainer>(new CounterId(1), new RecordingContainerMerge());
            Assert.Equal(new ContainerTypeId(0), vecId);
            Assert.Equal(new ContainerTypeId(1), setId);
            Assert.Equal(2, values.TypeCount);

            Assert.True(values.TryGetEnvironment<VecContainer>(out var env));
            Assert.Equal(vecId, values.RegisterType<VecContainer>(new CounterId(5), new RecordingContainerMerge()));
            Assert.Same(env, values.GetEnvironment<VecContainer>());
            Assert.Same(first, env.MergeFunction);
            Assert.Equal(new CounterId(0), env.Counter);
            Assert.Same(env, values.GetEnvironment(vecId));
            Assert.Equal(typeof(SetContainer), values.GetEnvironment(setId).ContainerType);
            Assert.True(values.TryGetTypeId<SetContainer>(out var found));
            Assert.Equal(setId, found);
        }

        [Fact]
        public void AnUnregisteredTypeHasNoValues()
        {
            var (db, _, _) = Setup();
            var values = db.Containers;
            Assert.False(values.TryGetEnvironment<VecContainer>(out _));
            Assert.False(values.TryGetTypeId<VecContainer>(out _));
            Assert.False(values.TryGetValue<VecContainer>(0, out _));
            var collector = new ContainerCollector<VecContainer>();
            values.ForEach<VecContainer, ContainerCollector<VecContainer>>(ref collector);
            Assert.Empty(collector.Items);

            var e = Assert.Throws<InvalidOperationException>(() => db.WithExecutionState(es => values.RegisterValue(new VecContainer(1), es)));
            Assert.Contains("must be registered", e.Message);
        }

        [Fact]
        public void RegisterValueAndTryGetValueRoundTrip()
        {
            var (db, uf, ids) = Setup();
            var values = db.Containers;
            values.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf));
            values.RegisterType<SetContainer>(ids, new UnionContainerMerge(uf));

            var (v, s, v2) = db.WithExecutionState(es => (
                values.RegisterValue(new VecContainer(4, 5), es),
                values.RegisterValue(new SetContainer(5, 4), es),
                es.Containers.RegisterValue(new VecContainer(4, 5), es)));

            // Both types draw from the one id counter, so their ids are disjoint.
            Assert.Equal(0u, v);
            Assert.Equal(1u, s);
            Assert.Equal(v, v2);
            Assert.True(values.TryGetValue<VecContainer>(v, out var vec));
            Assert.Equal(new VecContainer(4, 5), vec);
            Assert.True(values.TryGetValue<SetContainer>(s, out var set));
            Assert.Equal(new SetContainer(4, 5), set);
            Assert.False(values.TryGetValue<SetContainer>(v, out _));

            var collector = new ContainerCollector<SetContainer>();
            values.ForEach<SetContainer, ContainerCollector<SetContainer>>(ref collector);
            Assert.Equal((new SetContainer(4, 5), s), Assert.Single(collector.Items));
            values.CheckInvariants();
        }

        [Fact]
        public void RebuildAllWithoutARebuilderChangesNothing()
        {
            var (db, _, ids) = Setup();
            var plain = db.AddTable(Function(1, KeepCurrentMerge.Instance));
            db.Containers.RegisterType<VecContainer>(ids, new RecordingContainerMerge());
            db.WithExecutionState(es => db.Containers.RegisterValue(new VecContainer(1), es));

            var summary = db.WithExecutionState(es => db.Containers.RebuildAll(plain, db.GetTable(plain), es));
            Assert.False(summary.Changed);
            Assert.Equal(0, summary.DirtyCount);
        }

        [Fact]
        public void RebuildAllMergesEqualContainersThroughTheUnionFind()
        {
            var (db, uf, ids) = Setup();
            var clock = db.AddCounter();
            for (var i = 0; i < 7; i++)
                db.IncrementCounter(clock);

            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf, clock));
            var a = NewId(db, ids);
            var b = NewId(db, ids);
            var (va, vb) = db.WithExecutionState(es => (db.Containers.RegisterValue(new VecContainer(a), es), db.Containers.RegisterValue(new VecContainer(b), es)));
            Union(db, uf, a, b);

            var summary = RebuildAll(db, uf);
            Assert.True(summary.Changed);
            Assert.Equal(0, summary.DirtyCount);

            // The merge staged [va, vb, clock] and kept va; merging applies the union.
            Assert.True(db.MergeAll());
            Assert.Equal(va, Find(db, uf, vb));
            Assert.Equal(new[] { R(vb, va, 7) }, TableTestUtil.LiveRows(db.GetTable(uf)).Where(r => r[0] == vb).ToArray());
            Assert.True(db.Containers.TryGetValue<VecContainer>(va, out var merged));
            Assert.Equal(new VecContainer(a), merged);
            Assert.False(db.Containers.TryGetValue<VecContainer>(vb, out _));
            db.Containers.CheckInvariants();

            // A second rebuild finds nothing left to do.
            Assert.False(RebuildAll(db, uf).Changed);
        }

        /// <summary>
        /// With more than 1000 containers and few displaced ids, <see cref="ContainerValues.RebuildAll"/> revisits only
        /// the containers that mention a displaced id (egglog's <c>incremental_rebuild</c>); with many, it rebuilds
        /// every container.
        /// </summary>
        [Fact]
        public void RebuildAllIsIncrementalWhenFewIdsWereDisplaced()
        {
            var (db, uf, ids) = Setup();
            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf));
            var elements = Enumerable.Range(0, 2000).Select(_ => NewId(db, ids)).ToArray();
            var vecs = db.WithExecutionState(es => elements.Select(e => db.Containers.RegisterValue(new VecContainer(e), es)).ToArray());

            // The tracker's first look sees the whole (empty) union-find.
            VecContainer.TryRebuildCalls = 0;
            Assert.False(RebuildAll(db, uf).Changed);
            Assert.Equal(0, VecContainer.TryRebuildCalls);

            // One union: only the container holding the displaced element is revisited, and it merges into the other.
            Union(db, uf, elements[5], elements[6]);
            var summary = RebuildAll(db, uf);
            Assert.True(summary.Changed);
            Assert.Equal(1, VecContainer.TryRebuildCalls);
            db.MergeAll();
            Assert.Equal(vecs[5], Find(db, uf, vecs[6]));
            db.Containers.CheckInvariants();

            // The merge displaced vecs[6]: that one row is the next delta, with nothing to revisit.
            VecContainer.TryRebuildCalls = 0;
            Assert.False(RebuildAll(db, uf).Changed);
            Assert.Equal(0, VecContainer.TryRebuildCalls);

            // Three hundred unions are too many: every container is rebuilt.
            for (var i = 100; i < 400; i++)
                Stage(db, uf, R(elements[i], elements[i + 1000], 3));

            db.MergeAll();
            summary = RebuildAll(db, uf, 4);
            Assert.True(summary.Changed);
            Assert.Equal(1999, VecContainer.TryRebuildCalls);
            db.MergeAll();
            for (var i = 100; i < 400; i++)
                Assert.Equal(vecs[i], Find(db, uf, vecs[i + 1000]));

            db.Containers.CheckInvariants();
        }

        /// <summary>
        /// egglog's <c>expand_dirty_id_closure</c> (mod.rs:212-240): a container rebuilt in place makes every container
        /// holding it dirty, transitively and across container types.
        /// </summary>
        [Fact]
        public void TheDirtyIdClosureClimbsThroughEveryContainerType()
        {
            var (db, uf, ids) = Setup();
            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf));
            db.Containers.RegisterType<SetContainer>(ids, new UnionContainerMerge(uf));
            var k = NewId(db, ids);
            var w = NewId(db, ids);
            var (inner, outer, set, unrelated) = db.WithExecutionState(es =>
            {
                var i = db.Containers.RegisterValue(new VecContainer(w), es);
                var o = db.Containers.RegisterValue(new VecContainer(i, k), es);
                var s = db.Containers.RegisterValue(new SetContainer(o, 77), es);
                var u = db.Containers.RegisterValue(new VecContainer(k, k), es);
                return (i, o, s, u);
            });

            // Union k and w: inner rebuilds in place to [k], without changing its id.
            Union(db, uf, k, w);
            var summary = RebuildAll(db, uf);
            Assert.True(summary.Changed);
            Assert.Equal(new[] { inner, outer, set }, summary.DirtyIds.ToArray());
            Assert.False(summary.IsDirty(unrelated));
            Assert.True(db.Containers.TryGetValue<VecContainer>(inner, out var rebuilt));
            Assert.Equal(new VecContainer(k), rebuilt);
            db.Containers.CheckInvariants();
        }

        [Fact]
        public void TheClosureOfNoDirtyIdsIsEmpty()
        {
            var values = new ContainerValues(ParallelHeuristics.Serial);
            values.RegisterType<VecContainer>(new CounterId(0), new RecordingContainerMerge());
            var summary = new ContainerRebuildSummary();
            values.ExpandDirtyIdClosure(summary);
            Assert.False(summary.Changed);
            Assert.Equal(0, summary.DirtyCount);
        }

        /// <summary>
        /// Environments rebuild in parallel when there is more than one and more than one thread (egglog's
        /// <c>parallelize_inter_container_op</c>); the outcome (every container, its id and the union-find) matches the
        /// serial rebuild, and both report dirty ids, though not necessarily the same ones.
        /// </summary>
        [Fact]
        public void ParallelRebuildAcrossEnvironmentsMatchesSerial()
        {
            static (string State, uint[] Dirty, bool Changed) Run(ParallelHeuristics parallelism)
            {
                var (db, uf, ids) = Setup(parallelism);
                db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf));
                db.Containers.RegisterType<SetContainer>(ids, new UnionContainerMerge(uf));
                var e = Enumerable.Range(0, 60).Select(_ => NewId(db, ids)).ToArray();
                db.WithExecutionState(es =>
                {
                    for (var i = 0; i < 50; i++)
                    {
                        db.Containers.RegisterValue(new VecContainer(e[i], e[(i * 7) % 60]), es);
                        db.Containers.RegisterValue(new SetContainer(e[i], e[(i * 11) % 60], e[(i * 13) % 60]), es);
                    }

                    return 0;
                });

                for (var i = 0; i < 20; i++)
                    Stage(db, uf, R(e[i], e[i + 30], 1));

                db.MergeAll();
                var summary = RebuildAll(db, uf);
                db.MergeAll();
                db.Containers.CheckInvariants();

                var vecs = new ContainerCollector<VecContainer>();
                db.Containers.ForEach<VecContainer, ContainerCollector<VecContainer>>(ref vecs);
                var sets = new ContainerCollector<SetContainer>();
                db.Containers.ForEach<SetContainer, ContainerCollector<SetContainer>>(ref sets);
                var state = string.Join(" ", vecs.Items.Select(x => $"{x.Id}={x.Container}").Concat(sets.Items.Select(x => $"{x.Id}={x.Container}")).Order());
                var unions = string.Join(" ", e.Concat(Enumerable.Range(60, 100).Select(x => (uint)x)).Select(x => Find(db, uf, x)));
                return (state + " | " + unions, summary.DirtyIds.ToArray(), summary.Changed);
            }

            // The shard layouts differ, so containers are reinserted in a different order, which may change which ids
            // are reported dirty when stable containers merge (a superset is harmless); the outcome is the same.
            var serial = Run(ParallelHeuristics.Serial);
            var parallel = Run(new ParallelHeuristics(4));
            Assert.True(serial.Changed);
            Assert.True(parallel.Changed);
            Assert.Equal(serial.State, parallel.State);
            Assert.NotEmpty(serial.Dirty);
            Assert.NotEmpty(parallel.Dirty);
        }

        [Fact]
        public void CloneIsIndependent()
        {
            var (db, uf, ids) = Setup();
            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf));
            var a = NewId(db, ids);
            var b = NewId(db, ids);
            var v = db.WithExecutionState(es => db.Containers.RegisterValue(new VecContainer(b), es));
            var copy = db.Containers.Clone();

            Union(db, uf, a, b);
            RebuildAll(db, uf);
            db.WithExecutionState(es => db.Containers.RegisterValue(new VecContainer(a, a), es));

            Assert.True(copy.TryGetValue<VecContainer>(v, out var old));
            Assert.Equal(new VecContainer(b), old);
            Assert.Equal(1, copy.GetEnvironment<VecContainer>().Count);
            copy.CheckInvariants();
            Assert.True(db.Containers.TryGetValue<VecContainer>(v, out var current));
            Assert.Equal(new VecContainer(a), current);
            Assert.Equal(2, db.Containers.GetEnvironment<VecContainer>().Count);

            // The copy rebuilds on its own, from its own tracker.
            var summary = db.WithExecutionState(es => copy.RebuildAll(uf, db.GetTable(uf), es), timestamp: 2);
            Assert.Equal(new[] { v }, summary.DirtyIds.ToArray());
            Assert.True(copy.TryGetValue<VecContainer>(v, out var rebuilt));
            Assert.Equal(new VecContainer(a), rebuilt);
        }

        [Fact]
        public void TypesRegisteredAfterACloneStayOnTheirOwnSide()
        {
            // The two types registered after the clone take their slots before the type registered before it, so the
            // slot table the clone shares with its source has room for both: a registration that wrote the table in
            // place would show its type on the other side too. The tags are used by no other test.
            var sourceSlot = ContainerTypeSlot<TaggedContainer<DateTime>>.Index;
            var copySlot = ContainerTypeSlot<TaggedContainer<DateTimeOffset>>.Index;
            Assert.True(sourceSlot < ContainerTypeSlot<TaggedContainer<TimeSpan>>.Index);
            Assert.True(copySlot < ContainerTypeSlot<TaggedContainer<TimeSpan>>.Index);

            var ids = new CounterId(0);
            var merge = NoMergeContainerMerge.Instance;
            var source = new ContainerValues(ParallelHeuristics.Serial);
            var shared = source.RegisterType<TaggedContainer<TimeSpan>>(ids, merge);
            var copy = source.Clone();
            var sourceOnly = source.RegisterType<TaggedContainer<DateTime>>(ids, merge);
            var copyOnly = copy.RegisterType<TaggedContainer<DateTimeOffset>>(ids, merge);

            // Each side gives its own new type the next id, 1, and neither sees the other's.
            Assert.Equal(new ContainerTypeId(1), sourceOnly);
            Assert.Equal(new ContainerTypeId(1), copyOnly);
            Assert.False(copy.TryGetTypeId<TaggedContainer<DateTime>>(out _));
            Assert.False(copy.TryGetEnvironment<TaggedContainer<DateTime>>(out _));
            Assert.False(source.TryGetTypeId<TaggedContainer<DateTimeOffset>>(out _));
            Assert.False(source.TryGetEnvironment<TaggedContainer<DateTimeOffset>>(out _));

            // Id 1 names each side's own environment, by id and by type.
            Assert.True(source.TryGetEnvironment<TaggedContainer<DateTime>>(out var sourceEnv));
            Assert.Same(sourceEnv, source.GetEnvironment(sourceOnly));
            Assert.Equal(sourceOnly, sourceEnv.TypeId);
            Assert.True(copy.TryGetEnvironment<TaggedContainer<DateTimeOffset>>(out var copyEnv));
            Assert.Same(copyEnv, copy.GetEnvironment(copyOnly));
            Assert.Equal(copyOnly, copyEnv.TypeId);

            // The type registered before the clone keeps its id on both sides, in an environment of each side's own.
            Assert.Equal(2, source.TypeCount);
            Assert.Equal(2, copy.TypeCount);
            Assert.True(source.TryGetTypeId<TaggedContainer<TimeSpan>>(out var sourceShared));
            Assert.True(copy.TryGetTypeId<TaggedContainer<TimeSpan>>(out var copyShared));
            Assert.Equal(shared, sourceShared);
            Assert.Equal(shared, copyShared);
            Assert.Same(source.GetEnvironment(shared), source.GetEnvironment<TaggedContainer<TimeSpan>>());
            Assert.Same(copy.GetEnvironment(shared), copy.GetEnvironment<TaggedContainer<TimeSpan>>());
            Assert.NotSame(source.GetEnvironment(shared), copy.GetEnvironment(shared));
            source.CheckInvariants();
            copy.CheckInvariants();
        }

    }

}
