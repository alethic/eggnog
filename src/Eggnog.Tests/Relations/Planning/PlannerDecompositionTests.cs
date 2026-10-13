using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tree decomposition: the egglog defects this planner avoids (atoms without variables, lost join constraints,
    /// orphaned chain children, bags holding variables outside the bag), the pathwidth of the chain, determinism, and
    /// purity.
    /// </summary>
    public class PlannerDecompositionTests
    {

        static uint[][] Rows(string text) =>
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('(', ')').Split(',').Select(uint.Parse).ToArray()).ToArray();

        /// <summary>
        /// The lost-constraint query: nine atoms over three tables, from the random search of this suite (seed 30731),
        /// with the orphan-chain cases of egglog's <c>lost3.egg</c> absorbed by <c>all_children</c>.
        /// </summary>
        static (PlanTestDatabase Db, Query Query) LostConstraintQuery(uint[][] t0, uint[][] t1, uint[][] t2)
        {
            var db = new PlanTestDatabase();
            var T0 = db.AddTable(4, 4, t0, -1, [false, false, true, false]);
            var T1 = db.AddTable(1, 1, t1, -1, [true]);
            var T2 = db.AddTable(3, 3, t2);
            var qb = new QueryBuilder(db);
            var v = Enumerable.Range(0, 9).Select(i => qb.NewVariable()).ToArray();
            var w = QueryEntry.Wildcard();
            var c0 = new ColumnId(0);
            qb.AddAtom(T0, [v[4], v[4], v[2], v[3]]);
            qb.AddAtom(T2, [v[3], v[1], new Value(1)], [Constraint.Eq(c0, new ColumnId(1))]);
            qb.AddAtom(T1, [v[5]], [Constraint.Eq(c0, c0)]);
            qb.AddAtom(T2, [w, new Value(1), v[7]]);
            qb.AddAtom(T0, [v[2], v[7], v[7], v[5]], [Constraint.EqConst(new ColumnId(3), new Value(1))]);
            qb.AddAtom(T1, [v[4]]);
            qb.AddAtom(T0, [v[8], v[5], v[6], v[6]]);
            qb.AddAtom(T0, [w, v[1], v[2], v[0]]);
            qb.AddAtom(T2, [v[0], v[5], v[8]], [Constraint.GeConst(new ColumnId(2), new Value(1))]);
            foreach (var i in new[] { 1, 3, 5, 6, 7 })
                qb.MarkUsed(v[i]);

            return (db, qb.Build(new ActionId(0)));
        }

        [Fact]
        public void LostJoinConstraintIsKeptByAResultBlockLookup()
        {
            // Plan against one database (the statistics decide the decomposition) ...
            var (statsDb, query) = LostConstraintQuery(
                Rows("(1,3,2,2) (1,3,0,3) (0,2,2,0) (1,2,3,2) (0,1,3,3) (2,1,0,3) (1,1,2,0) (2,0,0,2) (2,3,3,1) (2,2,3,1) (3,2,0,2)"),
                Rows("(2) (1)"),
                Rows("(2,1,0) (0,0,1) (0,1,3) (2,2,3) (1,3,2) (1,1,3) (3,0,2)"));
            var plan = Assert.IsType<DecomposedPlan>(QueryPlanner.PlanQuery(query, statsDb));

            // Block 0's message variables are split across the later bags (v3 only in block 1, v1 only in block 2), so
            // it constrains neither, and it has no value variables: egglog's result block skips it (plan.rs:897-904,
            // 993-995).
            Assert.Equal("msg[v1,v2,v3,v5] val[]", plan.Blocks[0].Spec.ToString());
            Assert.DoesNotContain(plan.Blocks.Skip(1), b => b.Stages.Any(s => s is FusedIntersectMatStage { Cover.Rep: 0 }));
            Assert.Equal(
            [
                "Mat m2 Full bind[c0->v1, c1->v2, c2->v5, c3->v6, c4->v7]",
                "Mat m1 Value[v2,v5] bind[c0->v3]",
                "Mat m0 Lookup[v1,v2,v3,v5]",
            ], plan.ResultBlock.Select(s => s.ToString()));

            // ... and run it against data on which dropping that check derives a false match.
            var (db, _) = LostConstraintQuery(
                Rows("(2,2,0,0) (1,1,1,1) (2,2,0,1) (2,1,0,2) (0,0,2,2) (0,0,1,1)"),
                Rows("(1)"),
                Rows("(0,1,1) (0,0,1) (1,1,1)"));
            var output = query.GetUsedVariables();
            var expected = NaiveJoin.Evaluate(db, query, output);
            Assert.Equal(["1,1,1,1,1"], expected);
            Assert.True(new PlanInterpreter(db, plan, false).Run(output).SetEquals(expected));
            Assert.True(new PlanInterpreter(db, plan, true).Run(output).SetEquals(expected));

            var asEgglog = new DecomposedPlan(plan.Atoms, plan.VariableCount, plan.Headers, plan.ExistenceChecks, plan.Blocks,
                plan.ResultBlock.Where(s => s is not FusedIntersectMatStage { Mode: MatScanMode.Lookup }).ToArray(), plan.Action);
            var wrong = new PlanInterpreter(db, asEgglog, false).Run(output);
            Assert.Contains("0,1,1,1,1", wrong);
        }

        [Theory]
        [InlineData("path4", "PureSize")]
        [InlineData("path4", "MinCover")]
        [InlineData("rect", "PureSize")]
        [InlineData("rect", "MinCover")]
        [InlineData("path6", "PureSize")]
        [InlineData("path6", "MinCover")]
        [InlineData("tree5", "PureSize")]
        [InlineData("tree5", "MinCover")]
        [InlineData("spider", "PureSize")]
        [InlineData("spider", "MinCover")]
        public void FreeJoinWithDecompositionPlansCorrectly(string name, string strategy)
        {
            // egglog panics on path4 and rect ("Option::unwrap() on None" at plan.rs:1344/1491, "insert at index 3
            // exceeds fixedbitset size 3"): bag atoms hold variables outside the bag.
            var (db, query) = ExperimentQueries.Build(name, Enum.Parse<PlanStrategy>(strategy));
            var plan = Assert.IsType<DecomposedPlan>(QueryPlanner.PlanQuery(query, db));
            var output = query.GetUsedVariables();
            var expected = NaiveJoin.Evaluate(db, query, output);
            Assert.True(new PlanInterpreter(db, plan, false).Run(output).SetEquals(expected));
            Assert.True(new PlanInterpreter(db, plan, true).Run(output).SetEquals(expected));
        }

        [Fact]
        public void Path4PureSizeDecomposed()
        {
            var (db, query) = ExperimentQueries.Build("path4", PlanStrategy.PureSize);
            Assert.Equal(
            [
                "DecomposedPlan",
                "block 0 msg[v2] val[v4]",
                "[0] Intersect v2 [a1.c1, a2.c0]",
                "[1] FusedIntersect a3[c0,c1] bind[c0->v3, c1->v4] probe[a2[c1] key[0]]",
                "block 1 msg[] val[v0,v2]",
                "[0] Mat m0 KeyOnly bind[c0->v2] probe[a1[c1] key[0], a2[c0] key[0]]",
                "[1] Intersect v1 [a1.c0, a0.c1]",
                "[2] FusedIntersect a0[c0] bind[c0->v0]",
                "result",
                "[0] Mat m1 Full bind[c0->v0, c1->v2]",
                "[1] Mat m0 Value[v2] bind[c0->v4]",
            ], PlanLines.Of(QueryPlanner.PlanQuery(query, db)));
        }

        [Fact]
        public void OrphanedFirstChildIsAbsorbed()
        {
            // Bags 0 and 1 are both children of bag 2 (bag 0 shares x and c with it, bag 1 shares y), and bag 1
            // continues the chain: continuing through it keeps the widest bag at its own 3 atoms, while continuing
            // through bag 0 would fold bag 1's 3 atoms into the root (4). egglog's all_children[1..] (plan.rs:805)
            // skips bag 0, which then becomes a chain of its own: [bag0, bag1, bag2], joined through a Lookup epilogue.
            // Here bag 0 is absorbed into the chain node, giving [bag1, bag2 + bag0].
            //
            // (With the pathwidth score of plan.rs:735-740 corrected, giving bag 0 three atoms and bag 1 one would make
            // bag 0, children[0], the continuation, and nothing would be orphaned. The sizes are chosen so that the
            // orphan case is exercised.)
            Variable x = new(0), c = new(1), a = new(2), y = new(3), b = new(4);
            var bag0 = Bag([x, a, c], [0]);
            var bag1 = Bag([y, b], [1, 2, 3]);
            var bag2 = Bag([x, c, y], [4]);
            var chain = QueryPlanner.TopologicallySortBags([bag0, bag1, bag2]);
            Assert.Equal(2, chain.Count);
            Assert.Equal([y, b], chain[0].Variables.Select(kv => kv.Key).OrderBy(v => v.Index));
            Assert.Equal([1, 2, 3], chain[0].Atoms.Select(kv => kv.Key.Index));
            Assert.Equal([x, c, a, y], chain[1].Variables.Select(kv => kv.Key).OrderBy(v => v.Index));
            Assert.Equal([0, 4], chain[1].Atoms.Select(kv => kv.Key.Index));
        }

        [Fact]
        public void ChainParentTiesGoToTheLowestBag()
        {
            // Bag 0 shares one variable with bag 1 and one with bag 2: egglog's max_by_key on (count, -j) picks bag 1,
            // so the chain is 0 -> 1 -> 2. Picking bag 2 would make bags 0 and 1 siblings and absorb one of them.
            Variable x = new(0), y = new(1), z = new(2);
            var chain = QueryPlanner.TopologicallySortBags([Bag([x], [0]), Bag([x, y], [1]), Bag([x, z], [2])]);
            Assert.Equal([[0], [1], [2]], chain.Select(b => b.Atoms.Select(kv => kv.Key.Index).ToArray()));
        }

        [Fact]
        public void ChainHasTheMinimumPathwidthOverAllContinuations()
        {
            // Property test against brute force: for random bag trees (bag i's parent is the later bag it shares a
            // variable with), the chain's widest node equals the minimum, over every choice of continuation child at
            // every node, of the widest node the walk would build. This is the pathwidth egglog's doc comment promises
            // (plan.rs:704-716), which its swapped score (plan.rs:735-740) does not deliver.
            var rng = new Random(4242);
            for (var trial = 0; trial < 2000; trial++)
            {
                var n = rng.Next(2, 8);
                var parent = new int[n];
                var atomSets = new List<int>[n];
                var nextAtom = 0;
                for (var i = 0; i < n; i++)
                {
                    parent[i] = i == n - 1 || rng.Next(6) == 0 ? -1 : rng.Next(i + 1, n);
                    atomSets[i] = Enumerable.Range(nextAtom, rng.Next(1, 5)).ToList();
                    nextAtom += atomSets[i].Count;
                }

                // Occasionally share an atom with the parent: chain nodes count the union.
                for (var i = 0; i < n; i++)
                    if (parent[i] >= 0 && rng.Next(4) == 0)
                        atomSets[parent[i]].Add(atomSets[i][0]);

                // Bag i holds a private variable and an edge variable e_i shared with its parent only, so the parent
                // found by TopologicallySortBags (most shared variables, ties to the lowest index) is parent[i].
                var bags = new List<PlanningContext>(n);
                for (var i = 0; i < n; i++)
                {
                    var vars = new List<Variable> { new(2 * i) };
                    if (parent[i] >= 0)
                        vars.Add(new(2 * i + 1));
                    for (var k = 0; k < i; k++)
                        if (parent[k] == i)
                            vars.Add(new(2 * k + 1));

                    bags.Add(Bag(vars.OrderBy(v => v.Index).ToArray(), atomSets[i].Distinct().Order().ToArray()));
                }

                var expected = BruteForcePathwidth(parent, atomSets);
                var chain = QueryPlanner.TopologicallySortBags(bags);
                Assert.Equal(nextAtom, chain.SelectMany(b => b.Atoms.Select(kv => kv.Key.Index)).Distinct().Count());
                Assert.Equal(expected, chain.Max(b => b.Atoms.Count));
            }
        }

        static int BruteForcePathwidth(int[] parent, List<int>[] atomSets)
        {
            var n = parent.Length;
            var children = Enumerable.Range(0, n).Select(i => Enumerable.Range(0, n).Where(k => parent[k] == i).ToArray()).ToArray();
            HashSet<int> Full(int i) => atomSets[i].Concat(children[i].SelectMany(Full)).ToHashSet();

            var inner = Enumerable.Range(0, n).Where(i => children[i].Length > 0).ToArray();
            var choice = new int[n];
            var best = int.MaxValue;
            void Enumerate(int k)
            {
                if (k == inner.Length)
                {
                    var widest = 0;
                    for (var root = 0; root < n; root++)
                    {
                        if (parent[root] >= 0)
                            continue;

                        for (var node = root; ; node = children[node][choice[node]])
                        {
                            var width = atomSets[node].ToHashSet();
                            if (children[node].Length == 0)
                            {
                                widest = Math.Max(widest, width.Count);
                                break;
                            }

                            foreach (var sibling in children[node].Where((_, j) => j != choice[node]))
                                width.UnionWith(Full(sibling));

                            widest = Math.Max(widest, width.Count);
                        }
                    }

                    best = Math.Min(best, widest);
                    return;
                }

                for (var j = 0; j < children[inner[k]].Length; j++)
                {
                    choice[inner[k]] = j;
                    Enumerate(k + 1);
                }
            }

            Enumerate(0);
            return best;
        }

        static PlanningContext Bag(Variable[] vars, int[] atoms)
        {
            var vmap = new DenseIdMap<Variable, VarInfo>();
            foreach (var v in vars)
                vmap.Insert(v, new VarInfo());

            var amap = new DenseIdMap<AtomId, QueryAtom>();
            foreach (var atom in atoms)
                amap.Insert(new AtomId(atom), new QueryAtom(new TableId(0), new VarColumnMap(), [], []));

            return new PlanningContext(vmap, amap, new FunDeps(), new PlanTestDatabase(), new long[8], 8);
        }

        [Fact]
        public void VariableFreeAtomKeepsHeaderAndCheckUnderDecomposition()
        {
            // egglog drops atoms without variables from every bag, so its decomposed plans lose their headers
            // (plan.rs:498-506, 591-596) and never apply their slow constraints.
            var db = new PlanTestDatabase();
            var t = db.AddTable(2, 2, PlanTestDatabase.ExperimentRows(20, 10), -1, [false, true]);
            var tables = Enumerable.Range(0, 4).Select(i => db.AddTable(2, 2, PlanTestDatabase.ExperimentRows(10 + 10 * i, 3 + i))).ToArray();
            var qb = new QueryBuilder(db);
            var v = Enumerable.Range(0, 5).Select(i => qb.NewVariable()).ToArray();
            for (var i = 0; i < 4; i++)
                qb.AddAtom(tables[i], [v[i], v[i + 1]]);
            var constant = qb.AddAtom(t, [new Value(3), new Value(10)]);
            qb.MarkUsed(v[0]);
            qb.MarkUsed(v[4]);
            var query = qb.Build(new ActionId(0));

            var plan = Assert.IsType<DecomposedPlan>(QueryPlanner.PlanQuery(query, db));
            var header = Assert.Single(plan.Headers, h => h.Atom == constant);
            Assert.Equal("a4{c0 == 3}", header.ToString());
            var check = Assert.Single(plan.ExistenceChecks);
            Assert.Equal("exists a4{c1 == 10}", check.ToString());
            Assert.DoesNotContain(plan.Blocks.SelectMany(b => b.Stages).Concat(plan.ResultBlock), s => s.ToString()!.Contains("a4"));

            var single = QueryPlanner.PlanQuery(query, db, PlanStrategy.Gj, noDecomposition: true);
            Assert.Single(single.ExistenceChecks);
            Assert.Single(single.Headers, h => h.Atom == constant);
        }

        [Fact]
        public void PlanningIsDeterministicAndPure()
        {
            foreach (var name in ExperimentQueries.Names)
            {
                foreach (var strategy in new[] { PlanStrategy.Gj, PlanStrategy.PureSize, PlanStrategy.MinCover })
                {
                    var (db, query) = ExperimentQueries.Build(name, strategy);
                    var before = Snapshot(query);
                    var first = QueryPlanner.PlanQuery(query, db).ToString();
                    Assert.Equal(before, Snapshot(query));
                    for (var i = 0; i < 3; i++)
                        Assert.Equal(first, QueryPlanner.PlanQuery(query, db).ToString());

                    // A query rebuilt from scratch plans identically: nothing depends on hashing or object identity.
                    var (db2, query2) = ExperimentQueries.Build(name, strategy);
                    Assert.Equal(first, QueryPlanner.PlanQuery(query2, db2).ToString());
                }
            }
        }

        static string Snapshot(Query query)
        {
            var parts = new List<string>();
            foreach (var (v, info) in query.Variables)
                parts.Add($"{v}:{info.UsedInRhs}:{info.DefinedInRhs}:{string.Join(";", info.Occurrences)}");
            foreach (var (a, atom) in query.Atoms)
                parts.Add($"{a}:{atom}");

            return string.Join("|", parts);
        }

        [Fact]
        public void EliminationTiesGoToTheLowestVariable()
        {
            // A 4-cycle over tables with identical statistics: every variable has the same neighbourhood size and the
            // same uniqueness, so egglog's min_by_key keeps the first: v0. Its bag {v3, v0, v1} absorbs the atoms
            // touching it; the plan is fully determined by the id-order tie-breaks.
            var db = new PlanTestDatabase();
            var tables = Enumerable.Range(0, 4).Select(_ => db.AddTable(2, 2, PlanTestDatabase.ExperimentRows(10, 5))).ToArray();
            var qb = new QueryBuilder(db);
            var v = Enumerable.Range(0, 4).Select(i => qb.NewVariable()).ToArray();
            for (var i = 0; i < 4; i++)
                qb.AddAtom(tables[i], [v[i], v[(i + 1) % 4]]);
            foreach (var x in v)
                qb.MarkUsed(x);
            var query = qb.Build(new ActionId(0));

            var bags = QueryPlanner.DecomposeIntoBags(new PlanningContext(PlanningContext.CloneVariables(query.Variables), query.Atoms.Clone(), query.FunDeps, db, new long[4], 4));
            Assert.Equal([0, 1, 3], bags[0].Variables.Select(kv => kv.Key.Index));

            var plan = QueryPlanner.PlanQuery(query, db).ToString();
            for (var i = 0; i < 5; i++)
                Assert.Equal(plan, QueryPlanner.PlanQuery(query, db).ToString());
        }

        [Fact]
        public void PureSizeTiesKeepAtomOrder()
        {
            // Equal approximate sizes: Rust's stable sort keeps atom order, so a0 covers first.
            var db = new PlanTestDatabase();
            var tables = Enumerable.Range(0, 2).Select(_ => db.AddTable(2, 2, PlanTestDatabase.ExperimentRows(10, 5))).ToArray();
            var qb = new QueryBuilder(db);
            var v = Enumerable.Range(0, 3).Select(i => qb.NewVariable()).ToArray();
            qb.AddAtom(tables[0], [v[0], v[1]]);
            qb.AddAtom(tables[1], [v[1], v[2]]);
            var query = qb.Build(new ActionId(0));
            Assert.Equal(
            [
                "SinglePlan",
                "[0] FusedIntersect a0[c0,c1] bind[c0->v0, c1->v1] probe[a1[c0] key[1]]",
                "[1] FusedIntersect a1[c1] bind[c1->v2]",
            ], PlanLines.Of(QueryPlanner.PlanQuery(query, db, PlanStrategy.PureSize, false)));
        }

        [Fact]
        public void DecomposedHeadersAreInAtomOrder()
        {
            // PlanTemplate.Headers holds the fast constraints of each atom that has any, in atom order. A decomposed
            // plan collects them bag by bag, leaf bag first: path4 with fast constraints on a0 and a3 meets a3 (block
            // 0's bag {a1,a2,a3}) before a0 (block 1's bag {a0,a1,a2}), and still lists a0 first. Column uniqueness
            // uses whole-table numbers, so the constraints do not change the decomposition.
            var db = new PlanTestDatabase();
            (int N, int M)[] sizes = [(100, 30), (10, 5), (50, 20), (20, 10)];
            var tables = sizes.Select(t => db.AddTable(2, 2, PlanTestDatabase.ExperimentRows(t.N, t.M))).ToArray();
            var qb = new QueryBuilder(db);
            var v = Enumerable.Range(0, 5).Select(i => qb.NewVariable()).ToArray();
            qb.AddAtom(tables[0], [v[0], v[1]], [Constraint.EqConst(new ColumnId(0), new Value(1))]);
            qb.AddAtom(tables[1], [v[1], v[2]]);
            qb.AddAtom(tables[2], [v[2], v[3]]);
            qb.AddAtom(tables[3], [v[3], v[4]], [Constraint.EqConst(new ColumnId(1), new Value(4))]);
            qb.MarkUsed(v[0]);
            qb.MarkUsed(v[4]);
            var plan = Assert.IsType<DecomposedPlan>(QueryPlanner.PlanQuery(qb.Build(new ActionId(0)), db));

            var atoms = plan.Headers.Select(h => h.Atom.Index).ToArray();
            Assert.Equal(atoms.Order(), atoms);
        }

    }

}
