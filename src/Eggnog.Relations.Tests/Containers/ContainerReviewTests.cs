using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial review tests for the containers module: programs the model harness does not generate (unions of
    /// container ids themselves, with each other and with e-classes), and the database integration.
    /// </summary>
    public class ContainerReviewTests
    {

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
        /// derived <c>Clone</c>). A clone of the database must see the containers interned before it was taken, under the
        /// same ids, and be independent afterwards.
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
        /// <see cref="Database.RebuildContainers"/> (no timestamp) must therefore not stage a union older than the
        /// union-find's newest row. With a fixed default of 0 they staged the union at timestamp 0, which the union-find
        /// rejects as soon as any earlier union carried a later timestamp; the default is now the newest union's
        /// timestamp.
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
