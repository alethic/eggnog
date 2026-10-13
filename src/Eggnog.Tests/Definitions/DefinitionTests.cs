using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Definitions
{

    /// <summary>
    /// Definitions (§6.2, D6): names, types, roles, the engine layout of each kind, and definitions from other threads.
    /// </summary>
    public sealed class DefinitionTests
    {

        [Fact]
        public void DuplicateTableNamesAreRefused()
        {
            var graph = new EGraph();
            graph.DefineRelation<long>("t");
            Assert.Throws<ArgumentException>(() => graph.DefineRelation<long>("t"));
            Assert.Throws<ArgumentException>(() => graph.DefineFunction<long, long>("t"));
            Assert.Throws<ArgumentException>(() => graph.DefineRelation("contradiction"));
            Assert.Equal(2, graph.Tables.Count);
        }

        [Fact]
        public void InvalidTableNamesAreRefused()
        {
            var graph = new EGraph();
            Assert.Throws<ArgumentException>(() => graph.DefineRelation<long>("dom@schema"));
            Assert.Throws<ArgumentException>(() => graph.DefineRelation<long>("$internal"));
            Assert.Throws<ArgumentException>(() => graph.DefineRelation<long>(""));
            Assert.Throws<ArgumentNullException>(() => graph.DefineRelation<long>(null!));
            Assert.Single(graph.Tables);
        }

        [Fact]
        public void TheGraphsContradictionIsTheFirstTable()
        {
            var graph = new EGraph();
            Assert.Equal("contradiction", graph.Contradiction.Name);
            Assert.Same(graph.Contradiction, graph.Tables[0]);
            Assert.Equal(0, graph.Contradiction.Index);
            Assert.Equal(TableRole.Fact, graph.Contradiction.Role);
            Assert.Empty(graph.Contradiction.Keys);
            Assert.False(graph.Root.IsContradictory);
            graph.Root.Assume(graph.Contradiction.Of());
            Assert.True(graph.Root.IsContradictory);
        }

        [Fact]
        public void UnknownTypesAreRefused()
        {
            var graph = new EGraph();
            Assert.Contains("ValueStore.Register", Assert.Throws<ArgumentException>(() => graph.DefineRelation<Point>("p")).Message);
            Assert.Contains("ValueStore.RegisterEnum", Assert.Throws<ArgumentException>(() => graph.DefineFunction<string, Shade>("s")).Message);
            Assert.Contains("EGraph.DefineSort", Assert.Throws<ArgumentException>(() => graph.DefineConstructor<long, Node>("n")).Message);
            Assert.Single(graph.Tables);
        }

        [Fact]
        public void ASortOutputToDefineFunctionIsRefused()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            Assert.Contains("DefineConstructor", Assert.Throws<ArgumentException>(() => graph.DefineFunction<string, Node>("f")).Message);
        }

        [Fact]
        public void TheTermRoleIsRefusedForRelationsAndFunctions()
        {
            var graph = new EGraph();
            var term = new TableOptions { Role = TableRole.Term };
            Assert.Throws<ArgumentException>(() => graph.DefineRelation<long>("r", term));
            Assert.Throws<ArgumentException>(() => graph.DefineFunction<long, long>("f", options: term));
            graph.DefineSort<Node>("Node");
            Assert.Equal(TableRole.Term, graph.DefineConstructor<long, Node>("c", options: new TableOptions { Role = TableRole.Analysis }).Role);
        }

        [Fact]
        public void SortsAreDefinedOnce()
        {
            var graph = new EGraph();
            var node = graph.DefineSort<Node>("Node");
            Assert.Equal(ColumnKind.Sort, node.Kind);
            Assert.Equal("Node", node.Name);
            Assert.Same(node, graph.Values.TypeOf<Node>());
            Assert.Throws<ArgumentException>(() => graph.DefineSort<Node>("Other"));
            Assert.Throws<ArgumentException>(() => new EGraph().DefineSort<Node>("String"));
        }

        [Fact]
        public void ConstructorCostsMustNotBeNegative()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            Assert.Throws<ArgumentOutOfRangeException>(() => graph.DefineConstructor<Node>("c", cost: -1));
            var free = graph.DefineConstructor<Node>("free", cost: 0, options: new TableOptions { Extractable = false });
            Assert.Equal(0, free.Cost);
            Assert.False(free.Extractable);
        }

        [Fact]
        public void HandlesDescribeTheirTables()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var present = graph.DefineRelation<string>("present");
            var analysis = graph.DefineRelation<Node>("analysis", new TableOptions { Role = TableRole.Analysis });
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var lit = graph.DefineConstructor<long, Node>("Lit", cost: 3);
            var unlifted = graph.DefineRelation<string>("unlifted", new TableOptions { Lift = false });

            Assert.Equal(new TableHandle[] { graph.Contradiction, present, analysis, dom, lit, unlifted }, graph.Tables);
            Assert.Equal(1, present.Index);
            Assert.Equal(TableKind.Relation, present.Kind);
            Assert.Null(present.Output);
            Assert.True(present.Lift);
            Assert.True(present.IsValueOnly);
            Assert.Same(graph, present.Graph);
            Assert.Equal("present", present.ToString());
            Assert.False(analysis.Lift);
            Assert.False(analysis.IsValueOnly);
            Assert.Equal(TableKind.Function, dom.Kind);
            Assert.Same(graph.Values.TypeOf<ValueSet<string>>(), dom.Output);
            Assert.Equal("intersect", dom.Lattice!.Name);
            Assert.True(dom.IsValueOnly);
            Assert.Equal(TableKind.Constructor, lit.Kind);
            Assert.Equal(TableRole.Term, lit.Role);
            Assert.True(lit.Lift);
            Assert.True(lit.Extractable);
            Assert.Equal(3, lit.Cost);
            Assert.False(lit.IsValueOnly);
            Assert.False(unlifted.Lift);
        }

        [Fact]
        public void EachKindHasItsEngineLayout()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var r = graph.DefineRelation<string, Node>("r");
            var latticed = graph.DefineFunction<string, long>("latticed", ValueLattice.Max<long>());
            var plain = graph.DefineFunction<string, long>("plain");
            var c = graph.DefineConstructor<string, Node>("C");
            var unit = Relations.ColumnTy.Base(graph.BaseValues.GetTypeId<Relations.UnitCodec, ValueTuple>());
            var text = Relations.ColumnTy.Base(graph.BaseValues.GetTypeId<Relations.StringCodec, string>());
            var number = Relations.ColumnTy.Base(graph.BaseValues.GetTypeId<Relations.Int64Codec, long>());

            var relation = graph.Engine.GetFunctionInfo(r.Function);
            Assert.Equal("r", relation.Name);
            Assert.Equal(new[] { text, Relations.ColumnTy.Id, unit }, relation.Schema.ToArray());
            Assert.Equal(Relations.DefaultValKind.Const, relation.Default.Kind);
            Assert.Equal(graph.Unit.Unit, relation.Default.Constant.Rep);
            Assert.Equal(Relations.MergeFnKind.AssertEq, relation.Merge.Kind);
            Assert.False(relation.CanSubsume);

            var withLattice = graph.Engine.GetFunctionInfo(latticed.Function);
            Assert.Equal(new[] { text, number }, withLattice.Schema.ToArray());
            Assert.Equal(Relations.DefaultValKind.Fail, withLattice.Default.Kind);
            Assert.Equal(Relations.MergeFnKind.Primitive, withLattice.Merge.Kind);
            Assert.True(withLattice.Merge.IsLattice);
            Assert.Equal(Relations.MergeFnKind.Old, withLattice.Merge.Args[0].Kind);
            Assert.Equal(Relations.MergeFnKind.New, withLattice.Merge.Args[1].Kind);

            var withoutLattice = graph.Engine.GetFunctionInfo(plain.Function);
            Assert.Equal(Relations.DefaultValKind.Fail, withoutLattice.Default.Kind);
            Assert.Equal(Relations.MergeFnKind.AssertEq, withoutLattice.Merge.Kind);

            var constructor = graph.Engine.GetFunctionInfo(c.Function);
            Assert.Equal(new[] { text, Relations.ColumnTy.Id }, constructor.Schema.ToArray());
            Assert.Equal(Relations.DefaultValKind.FreshId, constructor.Default.Kind);
            Assert.Equal(Relations.MergeFnKind.UnionId, constructor.Merge.Kind);
        }

        [Fact]
        public void RulesetNamesAreUnique()
        {
            var graph = new EGraph();
            var a = graph.DefineRuleset("a");
            Assert.Throws<ArgumentException>(() => graph.DefineRuleset("a"));
            Assert.Throws<ArgumentException>(() => graph.DefineRuleset(""));
            Assert.Equal(new[] { a }, graph.Rulesets);
            Assert.Same(graph, a.Graph);
            Assert.Equal("a", a.ToString());
        }

        [Fact]
        public void ATableDefinedFromAnotherThreadWhileTheRootRunsThrows()
        {
            using var run = new BlockingRun();
            run.Start();
            var e = Assert.Throws<InvalidOperationException>(() => run.Graph.DefineRelation<long>("late"));
            Assert.Contains("in use on another thread", e.Message);
            run.Finish();
            Assert.NotNull(run.Graph.DefineRelation<long>("later"));
        }

        [Fact]
        public async Task ARuleDefinedFromAnotherThreadDuringARunJoinsTheNextIteration()
        {
            var graph = new EGraph();
            var tick = graph.DefineRelation<long>("tick");
            var seen = graph.DefineRelation<long>("seen");
            var rules = graph.DefineRuleset("rules");
            var started = new ManualResetEventSlim();
            var lateRan = new ManualResetEventSlim();
            var keepGoing = 1;
            rules.DefineRule("tick", r =>
            {
                var x = r.Var<long>("x");
                r.When(tick.Of(x));
                r.Where(x, _ =>
                {
                    started.Set();
                    return Volatile.Read(ref keepGoing) == 1;
                });
                r.Then(tick.Of(Primitives.Add.Of(x, 1L)));
            });
            graph.Root.Assume(tick.Of(0L));

            var run = DedicatedThread.Run(() => graph.Root.Run(Schedule.Saturate(rules), new RunOptions { IterationLimit = 1_000_000 }));
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            var late = rules.DefineRule("late", r =>
            {
                var x = r.Var<long>("x");
                r.When(tick.Of(x));
                r.Where(x, _ =>
                {
                    lateRan.Set();
                    return true;
                });
                r.Then(seen.Of(x));
            });
            Assert.True(lateRan.Wait(TimeSpan.FromSeconds(30)));
            Volatile.Write(ref keepGoing, 0);
            var report = await run;

            Assert.True(report.Saturated);
            Assert.True(report.Rules.ContainsKey(late));
            Assert.True(report.Rules[late].IterationsWithMatches > 0);
            Assert.True(graph.Root.Check(seen.Of(0L)));
        }

    }

}
