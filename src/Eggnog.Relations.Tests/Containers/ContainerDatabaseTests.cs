using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the containers of a <see cref="Database"/>: <see cref="Database.Containers"/>,
    /// <see cref="Database.RebuildContainers"/> (egglog's core-relations/src/free_join/mod.rs:349-363), and the bridge's
    /// native rebuild loop that runs containers first and re-stamps the parents of dirty containers
    /// (<see cref="Database.RebuildPass"/>, <see cref="Database.Rebuild"/>; egglog-bridge/src/lib.rs:626-682).
    /// </summary>
    public class ContainerDatabaseTests
    {

        /// <summary>
        /// A database with the union-find, the bridge's id counter and clock (starting at 1), the vector type registered
        /// with the bridge's merge, and function tables <c>[key, value, ts]</c> over ids with the union-id merge.
        /// </summary>
        static (Database Db, TableId Uf, CounterId Ids, CounterId Clock, TableId[] Tables) Setup(int tables = 1, ParallelHeuristics? parallelism = null)
        {
            var p = parallelism ?? ParallelHeuristics.Serial;
            var db = new Database(p);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var ids = db.AddCounter();
            var clock = db.AddCounter();
            db.IncrementCounter(clock);
            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf, clock));
            var result = new TableId[tables];
            for (var i = 0; i < tables; i++)
                result[i] = db.AddTable(Function(1, new UnionIdMerge(C(1), uf, C(2)), [C(0), C(1)], p), [], [uf], "f" + i);

            return (db, uf, ids, clock, result);
        }

        static uint Find(Database db, TableId uf, uint id) => ((DisplacedTable)db.GetTable(uf)).FindNaive(id);

        static uint NewId(Database db, CounterId ids) => (uint)db.IncrementCounter(ids);

        static uint Intern(Database db, params uint[] values) => db.WithExecutionState(es => es.Containers.RegisterValue(new VecContainer(values), es));

        [Fact]
        public void ContainersAreCreatedOnceAndSharedWithExecutionStates()
        {
            var db = new Database(new ParallelHeuristics(3));
            var containers = db.Containers;
            Assert.Same(containers, db.Containers);
            Assert.Same(containers, db.CreateExecutionState().Containers);
            Assert.Same(containers, db.CreateExecutionState().Clone().Containers);
            Assert.Same(db.Parallelism, containers.Parallelism);
        }

        [Fact]
        public void RebuildContainersWithoutTypesReportsNothing()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable());
            var summary = db.RebuildContainers(uf);
            Assert.False(summary.Changed);
            Assert.Equal(0, summary.DirtyCount);
            Assert.Throws<KeyNotFoundException>(() => db.RebuildContainers(new TableId(7)));
        }

        [Fact]
        public void ContainersAreUnavailableWhileTheyRebuild()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable());
            var ids = db.AddCounter();
            db.Containers.RegisterType<VecContainer>(ids, new ReentrantContainerMerge());
            var a = NewId(db, ids);
            var b = NewId(db, ids);
            Intern(db, a);
            Intern(db, b);
            Stage(db, uf, R(a, b, 1));
            db.MergeAll();

            var e = Assert.Throws<InvalidOperationException>(() => db.RebuildContainers(uf));
            Assert.Contains("being rebuilt", e.Message);
            Assert.NotNull(db.Containers);
        }

        /// <summary>
        /// The rebuild loop merges vectors whose elements became equal, and the union of their ids closes congruence in
        /// the function table that maps vectors to ids (the collapse egglog-bridge's <c>container_test</c> checks).
        /// </summary>
        [Fact]
        public void TheRebuildLoopCollapsesEqualVectorsAndClosesCongruence()
        {
            var (db, uf, ids, clock, tables) = Setup();
            var vec = tables[0];
            var a = NewId(db, ids);
            var b = NewId(db, ids);
            var va = Intern(db, a);
            var vb = Intern(db, b);
            var vaa = Intern(db, a, a);
            var vbb = Intern(db, b, b);
            var x = Enumerable.Range(0, 4).Select(_ => NewId(db, ids)).ToArray();
            Stage(db, vec, R(va, x[0], 1), R(vb, x[1], 1), R(vaa, x[2], 1), R(vbb, x[3], 1));
            db.MergeAll(1);
            Stage(db, uf, R(a, b, 1));
            db.MergeAll(1);

            var passes = db.Rebuild(uf, tables, clock);
            Assert.True(passes >= 2);
            Assert.Equal(1 + passes, db.ReadCounter(clock));
            Assert.Equal(va, Find(db, uf, vb));
            Assert.Equal(vaa, Find(db, uf, vbb));
            Assert.Equal(x[0], Find(db, uf, x[1]));
            Assert.Equal(x[2], Find(db, uf, x[3]));
            Assert.Equal(new[] { va, vaa }, Rows(db, vec).Select(r => r[0]).ToArray());
            Assert.Equal(new[] { x[0], x[2] }, Rows(db, vec).Select(r => r[1]).ToArray());

            var env = db.Containers.GetEnvironment<VecContainer>();
            Assert.Equal(2, env.Count);
            Assert.True(env.TryGetContainer(vaa, out var pair));
            Assert.Equal(new VecContainer(a, a), pair);
            db.Containers.CheckInvariants();
            Assert.Null(db.Failure);

            // Closed: one more pass changes nothing.
            Assert.Equal(1, db.Rebuild(uf, tables, clock));
        }

        /// <summary>
        /// A container rebuilt in place keeps its id, so the table rebuild leaves the rows mentioning it alone; the pass
        /// re-stamps them so seminaive evaluation sees them as new (egglog's <c>l(vec-of(w(k(b))))</c> example,
        /// core-relations/src/containers/mod.rs:86-89).
        /// </summary>
        [Fact]
        public void DirtyContainersHaveTheirParentRowsRestamped()
        {
            var (db, uf, ids, _, tables) = Setup();
            var l = tables[0];
            var k = NewId(db, ids);
            var w = NewId(db, ids);
            var v = Intern(db, w);
            var lv = NewId(db, ids);
            var lk = NewId(db, ids);
            Stage(db, l, R(v, lv, 1), R(k, lk, 1));
            db.MergeAll(1);
            Stage(db, uf, R(k, w, 2));
            db.MergeAll(2);

            Assert.True(db.RebuildPass(uf, tables, 5));
            Assert.Equal(new[] { R(k, lk, 1), R(v, lv, 5) }, Rows(db, l));
            Assert.True(db.Containers.TryGetValue<VecContainer>(v, out var rebuilt));
            Assert.Equal(new VecContainer(k), rebuilt);

            // The next pass has nothing to do.
            Assert.False(db.RebuildPass(uf, tables, 6));
            Assert.Equal(new[] { R(k, lk, 1), R(v, lv, 5) }, Rows(db, l));
        }

        /// <summary>
        /// The dirty-id closure reaches the parents of containers that hold a dirty container: with
        /// <c>(p (vec-of (vec-of (w (b)))))</c>, the outer vector's row is re-stamped too (egglog's comment,
        /// core-relations/src/containers/mod.rs:212-222).
        /// </summary>
        [Fact]
        public void NestedDirtyContainersHaveTheirParentRowsRestamped()
        {
            var (db, uf, ids, _, tables) = Setup(2);
            var (inner, outer) = (tables[0], tables[1]);
            var b = NewId(db, ids);
            var w = NewId(db, ids);
            var vin = Intern(db, w);
            var vout = Intern(db, vin);
            var pin = NewId(db, ids);
            var pout = NewId(db, ids);
            Stage(db, inner, R(vin, pin, 1));
            Stage(db, outer, R(vout, pout, 1));
            db.MergeAll(1);
            Stage(db, uf, R(b, w, 2));
            db.MergeAll(2);

            Assert.True(db.RebuildPass(uf, tables, 7));
            Assert.Equal(new[] { R(vin, pin, 7) }, Rows(db, inner));
            Assert.Equal(new[] { R(vout, pout, 7) }, Rows(db, outer));
        }

        /// <summary>
        /// The loop keeps going while containers change, even when the tables do not: merging two vectors unions their
        /// ids, which makes the vectors holding them equal on the next pass.
        /// </summary>
        [Fact]
        public void TheRebuildLoopRunsUntilNestedContainersSettle()
        {
            var (db, uf, ids, clock, _) = Setup(0);
            var a = NewId(db, ids);
            var b = NewId(db, ids);
            var va = Intern(db, a);
            var vb = Intern(db, b);
            var vva = Intern(db, va);
            var vvb = Intern(db, vb);
            var vvva = Intern(db, vva);
            var vvvb = Intern(db, vvb);
            Stage(db, uf, R(a, b, 1));
            db.MergeAll(1);

            var passes = db.Rebuild(uf, ReadOnlySpan<TableId>.Empty, clock);
            Assert.True(passes >= 3, $"only {passes} passes");
            Assert.Equal(va, Find(db, uf, vb));
            Assert.Equal(vva, Find(db, uf, vvb));
            Assert.Equal(vvva, Find(db, uf, vvvb));
            Assert.Equal(3, db.Containers.GetEnvironment<VecContainer>().Count);
            db.Containers.CheckInvariants();
        }

        [Fact]
        public void ARebuildPassWithNothingToDoChangesNothing()
        {
            var (db, uf, ids, _, tables) = Setup();
            var a = NewId(db, ids);
            Intern(db, a);
            Assert.False(db.RebuildPass(uf, tables, 3));
        }

        /// <summary>
        /// The unions a <see cref="UnionContainerMerge"/> without a timestamp counter stages carry the timestamp given to
        /// <see cref="Database.RebuildContainers"/>, or by default the timestamp of the union-find's newest union, never
        /// an older one (the union-find rejects those, as egglog asserts, core-relations/src/uf/mod.rs:499-505).
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData(9u)]
        public void RebuildContainersStampsUnionsWithTheGivenOrTheNewestTimestamp(uint? given)
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var ids = db.AddCounter();
            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf));
            var a = NewId(db, ids);
            var b = NewId(db, ids);
            var va = Intern(db, a);
            var vb = Intern(db, b);
            Stage(db, uf, R(a, b, 5));
            db.MergeAll(5);

            Assert.True(db.RebuildContainers(uf, given).Changed);
            db.MergeAll(9);
            var unionFind = ((DisplacedTable)db.GetTable(uf)).UnionFind;
            Assert.Equal(2, unionFind.Count);
            Assert.Equal(given ?? 5u, unionFind.GetTimestamp(1));
            Assert.Equal(va, Find(db, uf, vb));
            Assert.Null(db.Failure);
        }

        /// <summary>
        /// The copy <see cref="Database.Clone"/> makes keeps the containers and rebuilds them on its own: a union in the
        /// copy merges the copy's vectors (its rebuild tracker carried over, so it sees only the new union), and the
        /// original keeps both vectors.
        /// </summary>
        [Fact]
        public void ACloneRebuildsItsContainersIndependently()
        {
            var (db, uf, ids, clock, _) = Setup(0);
            var a = NewId(db, ids);
            var b = NewId(db, ids);
            var va = Intern(db, a);
            var vb = Intern(db, b);
            db.Rebuild(uf, ReadOnlySpan<TableId>.Empty, clock);

            var copy = db.Clone();
            Assert.NotSame(db.Containers, copy.Containers);
            Assert.Equal(1, copy.Containers.TypeCount);
            var ts = (uint)copy.ReadCounter(clock);
            Stage(copy, uf, R(a, b, ts));
            copy.MergeAll(ts);
            copy.IncrementCounter(clock);
            copy.Rebuild(uf, ReadOnlySpan<TableId>.Empty, clock);

            Assert.Equal(va, Find(copy, uf, vb));
            Assert.Equal(1, copy.Containers.GetEnvironment<VecContainer>().Count);
            copy.Containers.CheckInvariants();
            Assert.Null(copy.Failure);

            Assert.Equal(vb, Find(db, uf, vb));
            Assert.Equal(2, db.Containers.GetEnvironment<VecContainer>().Count);
            Assert.True(db.Containers.TryGetValue<VecContainer>(vb, out var original));
            Assert.Equal(new VecContainer(b), original);
            db.Containers.CheckInvariants();
        }

    }

}
