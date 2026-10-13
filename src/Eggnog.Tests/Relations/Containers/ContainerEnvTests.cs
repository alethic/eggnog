using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ContainerEnv{TContainer}"/>: the ported unit tests of egglog's
    /// core-relations/src/containers/tests.rs, and regression tests for the egglog defects the port fixes (map §8.1
    /// rows 13-15).
    /// </summary>
    public class ContainerEnvTests
    {

        static ContainerEnv<VecContainer> NewEnv(CounterId counter, ContainerMergeFunction? merge = null, ParallelHeuristics? parallelism = null)
        {
            return new ContainerEnv<VecContainer>(new ContainerTypeId(0), merge ?? NoMergeContainerMerge.Instance, counter, parallelism ?? ParallelHeuristics.Serial);
        }

        static VecContainer Get(ContainerEnv<VecContainer> env, uint id)
        {
            Assert.True(env.TryGetContainer(id, out var container), $"container {id} not found");
            return container;
        }

        /// <summary>
        /// egglog's <c>racing_inserts</c> (tests.rs:91-132): ten threads intern the same container at once; all get one
        /// id, and every result resolves to the container.
        /// </summary>
        [Fact]
        public void RacingInserts()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var env = NewEnv(counter, parallelism: ParallelHeuristics.Default);
            var start = new Notification();
            var results = new uint[10];
            var threads = ConcurrencyTestThread.StartMany(results.Length, i => db.WithExecutionState(es =>
            {
                start.Wait();
                results[i] = env.GetOrInsert(new VecContainer(1, 2, 3), es);
                return 0;
            }));
            start.Notify();
            ConcurrencyTestThread.JoinAll(threads);

            foreach (var result in results)
                Assert.Equal(new VecContainer(1, 2, 3), Get(env, result));

            Assert.True(results.All(r => r == results[0]), $"all containers should be the same, got [{string.Join(", ", results)}]");

            // The lookup and the insert share one lock, so only the winner minted an id; egglog's losers leave gaps.
            Assert.Equal(1, db.ReadCounter(counter));
            env.CheckInvariants();
        }

        /// <summary>
        /// egglog's <c>incremental_reinsert_canonicalizes_displaced_outer_id</c> (tests.rs:134-165): reinserting a
        /// container under a displaced outer id is a change but not a dirty id, and the container moves to the new id.
        /// </summary>
        [Fact]
        public void IncrementalReinsertCanonicalizesDisplacedOuterId()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var env = NewEnv(counter);
            var container = new VecContainer(1, 2, 3);

            db.WithExecutionState(es =>
            {
                var oldId = env.GetOrInsert(container, es);
                var newId = oldId + 1000;
                var taken = new List<ContainerEntry<VecContainer>>();
                Assert.Equal(1, env.TakeAll(oldId, taken));

                var summary = new ContainerRebuildSummary();
                env.ReinsertIncremental(taken[0].Container, taken[0].Hash, oldId, newId, false, es, summary);

                Assert.True(summary.Changed);
                Assert.Equal(0, summary.DirtyCount);
                Assert.False(env.TryGetContainer(oldId, out _));
                Assert.Equal(new VecContainer(1, 2, 3), Get(env, newId));

                // The index follows the id (egglog keeps the old id listed too).
                Assert.Equal(1, env.ValueIndex.CountOf(2, newId));
                Assert.Equal(0, env.ValueIndex.CountOf(2, oldId));
                env.CheckInvariants();
                return 0;
            });
        }

        /// <summary>
        /// egglog's <c>nonincremental_dirty_ids_only_include_stable_ids</c> (tests.rs:167-220): a full rebuild that
        /// changes a container's contents reports its id dirty only when the outer id stays the same.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NonincrementalDirtyIdsOnlyIncludeStableIds(bool outerIdChanges)
        {
            var db = new Database();
            var counter = db.AddCounter();
            const uint oldInner = 1;
            const uint newInner = 2;
            var env = NewEnv(counter);

            db.WithExecutionState(es =>
            {
                var oldId = env.GetOrInsert(new VecContainer(oldInner), es);
                var newId = outerIdChanges ? oldId + 1000 : oldId;
                var rebuilder = new FakeRebuilder(outerIdChanges ? oldId : null, outerIdChanges ? newId : null, oldInner, newInner);

                var summary = env.ApplyRebuildNonincremental(rebuilder, es);
                Assert.True(summary.Changed);
                if (outerIdChanges)
                {
                    Assert.Equal(0, summary.DirtyCount);
                    Assert.False(env.TryGetContainer(oldId, out _));
                    Assert.Equal(new VecContainer(newInner), Get(env, newId));
                }
                else
                {
                    Assert.Equal(new[] { oldId }, summary.DirtyIds.ToArray());
                    Assert.Equal(new VecContainer(newInner), Get(env, oldId));
                }

                env.CheckInvariants();
                return 0;
            });
        }

        [Fact]
        public void GetOrInsertMintsOneIdPerDistinctContainer()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var env = NewEnv(counter);

            db.WithExecutionState(es =>
            {
                var a = env.GetOrInsert(new VecContainer(1, 2), es);
                var b = env.GetOrInsert(new VecContainer(2, 1), es);
                var empty = env.GetOrInsert(new VecContainer(), es);
                var baseVec = env.GetOrInsert(new VecContainer(false, 1, 2), es);
                Assert.Equal(new uint[] { 0, 1, 2, 3 }, new[] { a, b, empty, baseVec });
                Assert.Equal(a, env.GetOrInsert(new VecContainer(1, 2), es));
                Assert.Equal(4, db.ReadCounter(counter));
                Assert.Equal(4, env.Count);

                Assert.True(env.TryGetId(new VecContainer(2, 1), out var found));
                Assert.Equal(b, found);
                Assert.False(env.TryGetId(new VecContainer(9), out _));
                Assert.False(env.TryGetContainer(99, out _));

                var collector = new ContainerCollector<VecContainer>();
                env.ForEach(ref collector);
                Assert.Equal(new uint[] { 0, 1, 2, 3 }, collector.Items.Select(x => x.Id).Order().ToArray());
                Assert.All(collector.Items, x => Assert.Equal(x.Container, Get(env, x.Id)));
                env.CheckInvariants();
                return 0;
            });
        }

        [Fact]
        public void GetOrInsertNeedsAStateOverADatabase()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var env = NewEnv(counter);
            var bare = new ExecutionState(new TableCatalog());

            Assert.Throws<InvalidOperationException>(() => env.GetOrInsert(new VecContainer(1), bare));
            Assert.Equal(0, env.Count);
            env.CheckInvariants();
        }

        [Fact]
        public void TheIndexCountsRepeatedValuesAndSurvivesRebuilds()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var env = NewEnv(counter);

            db.WithExecutionState(es =>
            {
                var id = env.GetOrInsert(new VecContainer(3, 3, 5), es);
                Assert.Equal(2, env.ValueIndex.CountOf(3, id));
                Assert.Equal(1, env.ValueIndex.CountOf(5, id));

                var summary = env.ApplyRebuildNonincremental(new FakeRebuilder(null, null, 3, 4), es);
                Assert.Equal(new[] { id }, summary.DirtyIds.ToArray());
                Assert.Equal(new VecContainer(4, 4, 5), Get(env, id));
                Assert.Equal(0, env.ValueIndex.CountOf(3, id));
                Assert.Equal(2, env.ValueIndex.CountOf(4, id));
                Assert.Equal(2, env.ValueIndex.ValueCount);
                env.CheckInvariants();
                return 0;
            });
        }

        [Fact]
        public void InsertOwnedMergesWithAnEqualContainer()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var merge = new RecordingContainerMerge();
            var env = NewEnv(counter, merge);

            db.WithExecutionState(es =>
            {
                for (var i = 0; i < 5; i++)
                    db.IncrementCounter(counter);

                var id = env.GetOrInsert(new VecContainer(7, 8), es);
                Assert.Equal(5u, id);

                // The existing id is smaller: it stays, and the incoming id is dropped.
                Assert.Equal(id, env.InsertOwned(new VecContainer(7, 8), id + 50, es));
                Assert.Equal((id, id + 50), merge.Calls.Single() is var c1 ? (c1.Current, c1.Incoming) : default);
                Assert.Equal(new VecContainer(7, 8), Get(env, id));
                Assert.False(env.TryGetContainer(id + 50, out _));

                // The incoming id is smaller: the entry moves to it, with its reverse-map entry and index occurrences.
                Assert.Equal(id - 3, env.InsertOwned(new VecContainer(7, 8), id - 3, es));
                Assert.Equal(new VecContainer(7, 8), Get(env, id - 3));
                Assert.False(env.TryGetContainer(id, out _));
                Assert.Equal(1, env.ValueIndex.CountOf(8, id - 3));
                Assert.Equal(0, env.ValueIndex.CountOf(8, id));
                Assert.Equal(1, env.Count);
                env.CheckInvariants();
                return 0;
            });
        }

        /// <summary>
        /// Map §8.1 row 13: egglog's full rebuild renames a container whose id alone changed without moving its element
        /// index entries (core-relations/src/containers/mod.rs:553-558), so a later incremental rebuild that starts from
        /// one of its elements looks up the old id, finds nothing and skips the container. Here the index follows the
        /// rename, and the incremental rebuild finds and rebuilds the container.
        /// </summary>
        [Fact]
        public void AnIdOnlyRenameKeepsTheIndexExactForIncrementalRebuilds()
        {
            var db = new Database();
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var counter = db.AddCounter();
            var env = NewEnv(counter, new RecordingContainerMerge());
            uint renamed = 0;

            db.WithExecutionState(es =>
            {
                for (var i = 0; i < 10; i++)
                    db.IncrementCounter(counter);

                var id = env.GetOrInsert(new VecContainer(5, 6), es);
                renamed = id + 100;
                var summary = env.ApplyRebuildNonincremental(new FakeRebuilder(id, renamed, null, null), es);
                Assert.True(summary.Changed);
                Assert.Equal(0, summary.DirtyCount);
                Assert.False(env.TryGetContainer(id, out _));
                Assert.Equal(new VecContainer(5, 6), Get(env, renamed));
                Assert.Equal(1, env.ValueIndex.CountOf(6, renamed));
                Assert.Equal(0, env.ValueIndex.CountOf(6, id));

                var holders = new OrderedValueSet();
                env.ExtendContainersContaining([6], holders);
                Assert.Equal(new[] { renamed }, holders.ToArray());
                env.CheckInvariants();
                return 0;
            });

            // Displace element 6 (by 5) and rebuild incrementally from the union-find's new rows.
            DatabaseTestUtil.Stage(db, uf, [5, 6, 1]);
            db.MergeAll();
            var table = db.GetTable(uf);
            var rebuilder = table.GetRebuilder(ReadOnlySpan<ColumnId>.Empty)!;
            var recent = table.UpdatesSince(0);
            db.WithExecutionState(es =>
            {
                var summary = env.ApplyRebuildIncremental(table, rebuilder, es, recent.AsRef(), new ColumnId(0));
                Assert.Equal(new[] { renamed }, summary.DirtyIds.ToArray());
                Assert.Equal(new VecContainer(5, 5), Get(env, renamed));
                env.CheckInvariants();
                return 0;
            });
        }

        /// <summary>
        /// A rename onto another container's id leaves two containers sharing the id, as in egglog; every structure stays
        /// exact, lookups by either container find the id, and a later rebuild takes both out.
        /// </summary>
        [Fact]
        public void ContainersSharingAnIdStayExact()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var merge = new RecordingContainerMerge();
            var env = NewEnv(counter, merge);

            db.WithExecutionState(es =>
            {
                var a = env.GetOrInsert(new VecContainer(1), es);
                var b = env.GetOrInsert(new VecContainer(2), es);
                var summary = env.ApplyRebuildNonincremental(new FakeRebuilder(b, a, null, null), es);
                Assert.True(summary.Changed);
                Assert.Equal(0, summary.DirtyCount);
                env.CheckInvariants();

                Assert.True(env.TryGetId(new VecContainer(1), out var idOf1));
                Assert.True(env.TryGetId(new VecContainer(2), out var idOf2));
                Assert.Equal(a, idOf1);
                Assert.Equal(a, idOf2);
                Assert.Contains(Get(env, a), new[] { new VecContainer(1), new VecContainer(2) });
                Assert.False(env.TryGetContainer(b, out _));

                var holders = new OrderedValueSet();
                env.ExtendContainersContaining([1, 2], holders);
                Assert.Equal(new[] { a }, holders.ToArray());

                // Rewriting 2 to 1 makes the two containers equal: they merge under the shared id, which stays, so it
                // is dirty.
                summary = env.ApplyRebuildNonincremental(new FakeRebuilder(null, null, 2, 1), es);
                Assert.Equal(new[] { a }, summary.DirtyIds.ToArray());
                Assert.Equal(1, env.Count);
                Assert.Equal(new VecContainer(1), Get(env, a));
                Assert.Equal((a, a), merge.Calls.Single() is var call ? (call.Current, call.Incoming) : default);
                env.CheckInvariants();
                return 0;
            });
        }

        [Fact]
        public void TakeAllTakesEveryContainerSharingTheId()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var env = NewEnv(counter, new RecordingContainerMerge());

            db.WithExecutionState(es =>
            {
                var a = env.GetOrInsert(new VecContainer(1), es);
                var b = env.GetOrInsert(new VecContainer(2), es);
                env.ApplyRebuildNonincremental(new FakeRebuilder(b, a, null, null), es);

                var taken = new List<ContainerEntry<VecContainer>>();
                Assert.Equal(2, env.TakeAll(a, taken));
                Assert.Equal(0, env.Count);
                Assert.Equal(0, env.ValueIndex.ValueCount);
                Assert.Equal(0, env.TakeAll(a, taken));
                Assert.Equal(0, env.TakeAll(12345, taken));
                env.CheckInvariants();
                return 0;
            });
        }

        /// <summary>
        /// Map §8.1 row 15: egglog's full rebuild goes parallel above the threshold for the number of environments (more
        /// than one container, mod.rs:526). Here it goes parallel only above the per-environment threshold of 1000
        /// containers: below it, merges run on the caller's state; above it, on a parallel task's clone.
        /// </summary>
        [Theory]
        [InlineData(2, false)]
        [InlineData(1000, false)]
        [InlineData(1001, true)]
        [InlineData(3000, true)]
        public void TheFullRebuildGoesParallelOnlyAboveTheIntraContainerThreshold(int containers, bool parallel)
        {
            var db = new Database();
            var counter = db.AddCounter();
            var merge = new RecordingContainerMerge();
            var env = NewEnv(counter, merge, new ParallelHeuristics(4));

            db.WithExecutionState(es =>
            {
                for (var i = 0u; i < containers; i++)
                    env.GetOrInsert(new VecContainer(i), es);

                // Rewriting the last element onto 0 makes the last container collide with the first.
                var summary = env.ApplyRebuildNonincremental(new FakeRebuilder(null, null, (uint)containers - 1, 0), es);
                Assert.True(summary.Changed);
                var call = Assert.Single(merge.Calls);
                Assert.Equal((0u, (uint)containers - 1), (call.Current, call.Incoming));
                if (parallel)
                    Assert.NotSame(es, call.State);
                else
                    Assert.Same(es, call.State);

                Assert.Equal(containers - 1, env.Count);
                Assert.Equal(0, summary.DirtyCount);
                Assert.False(env.TryGetContainer((uint)containers - 1, out _));
                env.CheckInvariants();
                return 0;
            });
        }

        [Fact]
        public void IsIncrementalFollowsEgglogsHeuristic()
        {
            Assert.False(ContainerEnv<VecContainer>.IsIncremental(0, 1000, false));
            Assert.True(ContainerEnv<VecContainer>.IsIncremental(0, 1001, false));
            Assert.True(ContainerEnv<VecContainer>.IsIncremental(125, 1001, false));
            Assert.False(ContainerEnv<VecContainer>.IsIncremental(126, 1001, false));
            Assert.True(ContainerEnv<VecContainer>.IsIncremental(1, 1001, true));
            Assert.False(ContainerEnv<VecContainer>.IsIncremental(2, 1001, true));
            Assert.True(ContainerEnv<VecContainer>.IsIncremental(2, 1024, true));
        }

        /// <summary>
        /// Map §8.1 row 14: egglog's lookup by id takes the same shard read lock twice and returns a guard, which can
        /// deadlock against a queued writer. Here it takes one lock once and returns the immutable container. Readers
        /// and writers hammer the environment together; every lookup of a published id succeeds.
        /// </summary>
        [Fact]
        public void LookupsByIdRunAlongsideConcurrentInterning()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var env = NewEnv(counter, parallelism: new ParallelHeuristics(4));
            var published = new uint[4][];
            var start = new Notification();
            var threads = ConcurrencyTestThread.StartMany(8, t =>
            {
                start.Wait();
                if (t < 4)
                {
                    var ids = new uint[2000];
                    db.WithExecutionState(es =>
                    {
                        for (var i = 0u; i < ids.Length; i++)
                            ids[i] = env.GetOrInsert(new VecContainer(i % 1500, i % 7), es);

                        return 0;
                    });
                    published[t] = ids;
                }
                else
                {
                    for (var round = 0; round < 20_000; round++)
                    {
                        var id = (uint)(round % 1600);
                        if (env.TryGetContainer(id, out var container))
                            Assert.True(env.TryGetId(container, out var back) && back == id);
                    }
                }
            });
            start.Notify();
            ConcurrencyTestThread.JoinAll(threads);

            // Equal containers got equal ids across threads, and no id was wasted.
            for (var i = 0; i < 2000; i++)
                Assert.True(published.All(ids => ids[i] == published[0][i]));

            var distinct = Enumerable.Range(0, 2000).Select(i => ((uint)(i % 1500), (uint)(i % 7))).Distinct().Count();
            Assert.Equal(distinct, env.Count);
            Assert.Equal(distinct, db.ReadCounter(counter));
            env.CheckInvariants();
        }

        [Fact]
        public void CloneIsIndependent()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var env = NewEnv(counter, new RecordingContainerMerge());

            db.WithExecutionState(es =>
            {
                var a = env.GetOrInsert(new VecContainer(1, 2), es);
                var copy = (ContainerEnv<VecContainer>)env.Clone();
                env.GetOrInsert(new VecContainer(3), es);
                env.ApplyRebuildNonincremental(new FakeRebuilder(null, null, 2, 9), es);

                Assert.Equal(1, copy.Count);
                Assert.Equal(new VecContainer(1, 2), Get(copy, a));
                Assert.Equal(1, copy.ValueIndex.CountOf(2, a));
                Assert.Equal(0, copy.ValueIndex.CountOf(9, a));
                copy.CheckInvariants();

                Assert.Equal(new VecContainer(1, 9), Get(env, a));
                env.CheckInvariants();
                return 0;
            });
        }

        [Fact]
        public void AFullRebuildMergesContainersThatBecomeEqual()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var merge = new RecordingContainerMerge();
            var env = NewEnv(counter, merge);

            db.WithExecutionState(es =>
            {
                var a = env.GetOrInsert(new VecContainer(10, 11), es);
                var b = env.GetOrInsert(new VecContainer(10, 12), es);
                var c = env.GetOrInsert(new VecContainer(12), es);
                var summary = env.ApplyRebuildNonincremental(new FakeRebuilder(null, null, 12, 11), es);

                Assert.True(summary.Changed);
                Assert.Equal(2, env.Count);
                Assert.Equal(new VecContainer(10, 11), Get(env, a));
                Assert.False(env.TryGetContainer(b, out _));
                Assert.Equal(new VecContainer(11), Get(env, c));

                // b merged into a (a is smaller): b's id does not survive, so it is not dirty; c changed in place.
                Assert.Equal((a, b), merge.Calls.Single() is var call ? (call.Current, call.Incoming) : default);
                Assert.Equal(new[] { c }, summary.DirtyIds.ToArray());
                env.CheckInvariants();
                return 0;
            });
        }

    }

}
