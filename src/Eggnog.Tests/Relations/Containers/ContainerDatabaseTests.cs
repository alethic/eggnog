using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the containers of a <see cref="Database"/>: <see cref="Database.Containers"/>,
    /// <see cref="Database.RebuildContainers"/> (egglog's core-relations/src/free_join/mod.rs:349-363), and the
    /// bridge's native rebuild loop that runs containers first and re-stamps the parents of dirty containers
    /// (<see cref="Database.RebuildPass"/>, <see cref="Database.Rebuild"/>; egglog-bridge/src/lib.rs:626-682).
    /// </summary>
    public class ContainerDatabaseTests
    {

        /// <summary>
        /// A database with the union-find, the bridge's id counter and clock (starting at 1), the vector type
        /// registered with the bridge's merge, and function tables <c>[key, value, ts]</c> over ids with the union-id
        /// merge.
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
        /// A container rebuilt in place keeps its id, so the table rebuild leaves the rows mentioning it alone; the
        /// pass re-stamps them so seminaive evaluation sees them as new (egglog's <c>l(vec-of(w(k(b))))</c> example,
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
        /// The unions a <see cref="UnionContainerMerge"/> without a timestamp counter stages carry the timestamp given
        /// to <see cref="Database.RebuildContainers"/>, or by default the timestamp of the union-find's newest union,
        /// never an older one (the union-find rejects those, as egglog asserts, core-relations/src/uf/mod.rs:499-505).
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
        /// The copy <see cref="Database.Clone"/> makes keeps the containers and rebuilds them on its own: a union in
        /// the copy merges the copy's vectors (its rebuild tracker carried over, so it sees only the new union), and
        /// the original keeps both vectors.
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

        /// <summary>
        /// The cases: degrees of parallelism 1 and 4, each with ten small programs and three large ones.
        /// </summary>
        public static TheoryData<int, int, bool> Cases()
        {
            var data = new TheoryData<int, int, bool>();
            foreach (var dop in new[] { 1, 4 })
                foreach (var large in new[] { false, true })
                    for (var seed = 0; seed < (large ? 3 : 10); seed++)
                        data.Add(dop, seed, large);

            return data;
        }

        /// <summary>
        /// Unions between arbitrary ids, container ids included, so that containers with different contents come to
        /// share an id (the reverse map's multimap case) and containers get renamed without changing contents. After
        /// every rebuild loop the structures must be exact, every stored id and element canonical, and every interned
        /// container findable under the canonical id of the class it was interned with.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cases))]
        public void UnionsOfContainerIdsKeepEveryStructureExactAndCanonical(int dop, int seed, bool large)
        {
            var random = new Random(seed * 31337 + dop * 7 + (large ? 1 : 0));
            var parallelism = dop == 1 ? ParallelHeuristics.Serial : new ParallelHeuristics(dop);
            var db = new Database(parallelism);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var ids = db.AddCounter();
            var clock = db.AddCounter();
            db.IncrementCounter(clock);
            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf, clock));
            db.Containers.RegisterType<SetContainer>(ids, new UnionContainerMerge(uf, clock));
            var find = (DisplacedTable)db.GetTable(uf);
            var eclasses = new List<uint>();
            var interned = new List<(char Kind, uint[] Contents, uint Id)>();

            uint NewEClass()
            {
                var id = (uint)db.IncrementCounter(ids);
                eclasses.Add(id);
                return id;
            }

            uint Pick(bool allowContainers)
            {
                if (allowContainers && interned.Count > 0 && random.Next(10) < 4)
                    return find.FindNaive(interned[random.Next(interned.Count)].Id);

                return find.FindNaive(eclasses[random.Next(eclasses.Count)]);
            }

            void Intern(char kind, int length)
            {
                var contents = Enumerable.Range(0, length).Select(_ => Pick(true)).ToArray();
                var id = db.WithExecutionState(es => kind == 's'
                    ? es.Containers.RegisterValue(new SetContainer(contents), es)
                    : es.Containers.RegisterValue(new VecContainer(contents), es));
                interned.Add((kind, contents, id));
            }

            void Rebuild()
            {
                var ts = (uint)db.ReadCounter(clock);
                db.MergeAll(ts);
                db.IncrementCounter(clock);
                var passes = db.Rebuild(uf, ReadOnlySpan<TableId>.Empty, clock);
                Assert.True(passes < 100);
                Assert.Null(db.Failure);
            }

            void Check()
            {
                db.Containers.CheckInvariants();
                var vecs = new ContainerCollector<VecContainer>();
                db.Containers.ForEach<VecContainer, ContainerCollector<VecContainer>>(ref vecs);
                var sets = new ContainerCollector<SetContainer>();
                db.Containers.ForEach<SetContainer, ContainerCollector<SetContainer>>(ref sets);
                foreach (var (vec, id) in vecs.Items)
                {
                    Assert.Equal(find.FindNaive(id), id);
                    Assert.All(vec.Values.ToArray(), v => Assert.Equal(find.FindNaive(v), v));
                }

                foreach (var (set, id) in sets.Items)
                {
                    Assert.Equal(find.FindNaive(id), id);
                    Assert.All(set.Values.ToArray(), v => Assert.Equal(find.FindNaive(v), v));
                }

                var distinct = new HashSet<string>();
                foreach (var (kind, contents, id) in interned)
                {
                    var canonical = contents.Select(find.FindNaive).ToArray();
                    uint found;
                    if (kind == 's')
                    {
                        var set = new SetContainer(canonical);
                        Assert.True(db.Containers.GetEnvironment<SetContainer>().TryGetId(set, out found), $"{set} is missing");
                        distinct.Add("s" + set);
                    }
                    else
                    {
                        var vec = new VecContainer(canonical);
                        Assert.True(db.Containers.GetEnvironment<VecContainer>().TryGetId(vec, out found), $"{vec} is missing");
                        distinct.Add("v" + vec);
                    }

                    Assert.Equal(find.FindNaive(id), found);
                }

                Assert.Equal(distinct.Count, vecs.Items.Count + sets.Items.Count);
            }

            var eclassCount = large ? 700 : 30;
            for (var i = 0; i < eclassCount; i++)
                NewEClass();

            var initial = large ? 1500 : 40;
            for (var i = 0; i < initial; i++)
                Intern(random.Next(4) == 0 ? 's' : 'v', random.Next(1, 4));

            Rebuild();
            Check();
            var rounds = large ? 20 : 40;
            for (var round = 0; round < rounds; round++)
            {
                var unions = large ? random.Next(1, random.Next(3) == 0 ? 200 : 4) : random.Next(1, 4);
                var ts = (uint)db.ReadCounter(clock);
                for (var u = 0; u < unions; u++)
                {
                    var a = Pick(true);
                    var b = Pick(random.Next(2) == 0);
                    Stage(db, uf, R(a, b, ts));
                }

                for (var i = random.Next(0, 4); i > 0; i--)
                    Intern(random.Next(4) == 0 ? 's' : 'v', random.Next(1, 4));

                Rebuild();
                Check();
            }
        }

        /// <summary>
        /// egglog's <c>Database::clone</c> copies <c>container_values</c> (core-relations/src/free_join/mod.rs, the
        /// derived <c>Clone</c>). A clone of the database must see the containers interned before it was taken, under
        /// the same ids, and be independent afterwards.
        /// </summary>
        [Fact]
        public void ACloneOfTheDatabaseKeepsItsContainers()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var ids = db.AddCounter();
            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf));
            var a = (uint)db.IncrementCounter(ids);
            var v = db.WithExecutionState(es => es.Containers.RegisterValue(new VecContainer(a), es));

            var copy = db.Clone();
            Assert.True(copy.Containers.TryGetTypeId<VecContainer>(out _), "the clone lost the registered container types");
            Assert.True(copy.Containers.TryGetValue<VecContainer>(v, out var found), "the clone lost the interned containers");
            Assert.Equal(new VecContainer(a), found);

            // Interning the same container in the clone finds it rather than minting a second id for it.
            var again = copy.WithExecutionState(es => es.Containers.RegisterValue(new VecContainer(a), es));
            Assert.Equal(v, again);

            // Independent afterwards.
            copy.WithExecutionState(es => es.Containers.RegisterValue(new VecContainer(a, a), es));
            Assert.Equal(1, db.Containers.GetEnvironment<VecContainer>().Count);
        }

        /// <summary>
        /// egglog's <c>rebuild_containers(table_id)</c> takes no timestamp: the bridge's merge reads the timestamp
        /// counter, so the unions it stages are never stamped below the union-find's last row. The defaults of
        /// <see cref="UnionContainerMerge"/> (no counter: stamp with the state's timestamp) and
        /// <see cref="Database.RebuildContainers"/> (no timestamp) stage a union at the newest union's timestamp, never
        /// older than the union-find's newest row; a union staged at timestamp 0 would be rejected by the union-find as
        /// soon as any earlier union carried a later timestamp.
        /// </summary>
        [Fact]
        public void RebuildContainersWithItsDefaultTimestampStagesAUsableUnion()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var ids = db.AddCounter();
            db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf));
            var a = (uint)db.IncrementCounter(ids);
            var b = (uint)db.IncrementCounter(ids);
            var va = db.WithExecutionState(es => es.Containers.RegisterValue(new VecContainer(a), es));
            var vb = db.WithExecutionState(es => es.Containers.RegisterValue(new VecContainer(b), es));
            Stage(db, uf, R(a, b, 5));
            db.MergeAll(5);

            var summary = db.RebuildContainers(uf);
            Assert.True(summary.Changed);
            db.MergeAll(6);
            Assert.Equal(((DisplacedTable)db.GetTable(uf)).FindNaive(va), ((DisplacedTable)db.GetTable(uf)).FindNaive(vb));
        }

    }

}
