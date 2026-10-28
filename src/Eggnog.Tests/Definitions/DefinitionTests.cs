using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Definitions
{

    /// <summary>
    /// Definitions: names, types, roles, the engine layout of each kind, and definitions from other threads. Rule and
    /// query definitions may be made from any thread at any time; a table definition, and a rule or query whose first
    /// scoped read creates a mirror table, is refused from another thread while the writable root is in use.
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
            Assert.Equal("contradiction", graph.DefineRelation("contradiction").Name);
            Assert.Equal(3, graph.Tables.Count);
        }

        [Fact]
        public void InvalidTableNamesAreRefused()
        {
            var graph = new EGraph();
            Assert.Throws<ArgumentException>(() => graph.DefineRelation<long>(""));
            Assert.Throws<ArgumentNullException>(() => graph.DefineRelation<long>(null!));
            Assert.Single(graph.Tables);
        }

        /// <summary>
        /// As in egglog, a table's name may hold <c>'@'</c> anywhere and start with <c>'$'</c>, and such a table is
        /// written, read and matched as any other. egglog's answer:
        /// <c>(relation r@x (i64)) (relation $f (i64)) (relation @bad (i64)) (r@x 1) ($f 2) (@bad 3)
        /// (rule ((r@x a)) (($f a))) (run 1) (check ($f 1))</c> succeeds, and <c>(print-function $f 10)</c> prints
        /// <c>($f 2)</c> and <c>($f 1)</c>.
        /// </summary>
        [Fact]
        public void TableNamesMayHoldAtSignsAndStartWithADollarAsInEgglog()
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<long>("r@x");
            var f = graph.DefineRelation<long>("$f");
            var bad = graph.DefineRelation<long>("@bad");
            var g = graph.DefineFunction<long, long>("g@h");
            Assert.Equal(new[] { "r@x", "$f", "@bad", "g@h" }, new[] { r.Name, f.Name, bad.Name, g.Name });
            Assert.Throws<ArgumentException>(() => graph.DefineRelation<long>("r@x"));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("copy", rule =>
            {
                var a = rule.Var<long>("a");
                rule.When(r.Of(a));
                rule.Then(f.Of(a));
            });
            graph.Root.Assume(r.Of(1L), f.Of(2L), bad.Of(3L), g.Of(1L).Is(5L));

            graph.Root.Run(Schedule.Saturate(rules));

            Assert.True(graph.Root.Check(f.Of(1L)));
            Assert.Equal(2, graph.Root.CountRows(f));
            Assert.True(graph.Root.Check(bad.Of(3L)));
            Assert.True(graph.Root.Check(g.Of(1L).Is(5L)));
            Assert.Equal("(rule ((r@x a)) (($f a)) :ruleset rules)", rules.Rules[0].ToString());
        }

        /// <summary>
        /// The built-in <see cref="EGraph.Contradiction"/> takes no name of the user's: a user's table named
        /// <c>$contradiction</c>, which egglog accepts (<c>(relation $contradiction ()) ($contradiction)
        /// (check ($contradiction))</c> succeeds), is another table, and holding it is no contradiction.
        /// </summary>
        [Fact]
        public void AUsersTableMayBeNamedAsTheBuiltInContradiction()
        {
            var graph = new EGraph();
            var own = graph.DefineRelation("$contradiction");
            Assert.NotSame(graph.Contradiction, own);
            Assert.Equal(graph.Contradiction.Name, own.Name);
            Assert.Throws<ArgumentException>(() => graph.DefineRelation("$contradiction"));
            Assert.Equal(new TableHandle[] { graph.Contradiction, own }, graph.Tables);

            graph.Root.Assume(own.Of());

            Assert.True(graph.Root.Check(own.Of()));
            Assert.False(graph.Root.IsContradictory);
            Assert.False(graph.Root.Check(graph.Contradiction.Of()));
            graph.Root.Assume(graph.Contradiction.Of());
            Assert.True(graph.Root.IsContradictory);
        }

        [Fact]
        public void TheGraphsContradictionIsTheFirstTable()
        {
            var graph = new EGraph();
            Assert.Equal("$contradiction", graph.Contradiction.Name);
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

        /// <summary>
        /// A function may output a sort, as egglog's function of an e-class sort, but its merge cannot decode classes: a
        /// lattice or a merge function is refused, and only the default, <c>New</c> and <c>Old</c> are left.
        /// </summary>
        [Fact]
        public void ASortOutputToDefineFunctionTakesNoLatticeAndNoMergeFunction()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            graph.DefineFunction<string, Node>("f");
            graph.DefineFunction<string, Node>("newest", FunctionMerge.New<Node>());
            var lattice = Assert.Throws<ArgumentException>(() => graph.DefineFunction<string, Node>("g", new ValueLattice<Node>("first", (a, b) => a)));
            var merge = Assert.Throws<ArgumentException>(() => graph.DefineFunction<string, Node>("h", FunctionMerge.Of<Node>("first", (a, b) => a)));
            Assert.Contains("merges values, not classes", lattice.Message, StringComparison.Ordinal);
            Assert.Contains("only FunctionMerge.New, Old and Expression merge classes", merge.Message, StringComparison.Ordinal);
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
        public void AConstructorMayCostNothing()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var free = graph.DefineConstructor<Node>("free", cost: 0, options: new TableOptions { Extractable = false });
            Assert.Equal<ulong>(0, free.Cost);
            Assert.False(free.Extractable);
        }

        [Fact]
        public void HandlesDescribeTheirTables()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var present = graph.DefineRelation<string>("present");
            var analysis = graph.DefineRelation<Node>("analysis", new TableOptions { Role = TableRole.Analysis });
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
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
            Assert.Same(graph.Values.TypeOf<Set<string>>(), dom.Output);
            Assert.Equal("intersect", dom.Lattice!.Name);
            Assert.True(dom.IsValueOnly);
            Assert.Equal(TableKind.Constructor, lit.Kind);
            Assert.Equal(TableRole.Term, lit.Role);
            Assert.True(lit.Lift);
            Assert.True(lit.Extractable);
            Assert.Equal<ulong>(3, lit.Cost);
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
            Assert.True(relation.CanSubsume);

            Assert.False(graph.Engine.GetFunctionInfo(plain.Function).CanSubsume);
            Assert.True(graph.Engine.GetFunctionInfo(c.Function).CanSubsume);

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

        /// <summary>
        /// A rule or query definition whose first scoped read creates a mirror is a table definition, so from another
        /// thread while the writable root is in use it throws <see cref="InvalidOperationException"/>.
        /// </summary>
        [Fact]
        public void AFirstScopedReadFromAnotherThreadWhileTheRootRunsIsRefused()
        {
            using var run = new BlockingRun();
            var tag = run.Graph.DefineScopeTag("outer");
            run.Start();

            var rule = Record.Exception(() => run.Rules.DefineRule("scoped", r =>
            {
                var x = r.Var<long>("x");
                r.When(run.Seed.In(tag).Of(x));
                r.Then(run.Done.Of(x));
            }));
            var query = Record.Exception(() => run.Graph.DefineQuery("scoped", q => q.When(run.Done.In(tag).Of(q.Var<long>("x")))));
            run.Finish();

            Assert.IsType<InvalidOperationException>(rule);
            Assert.IsType<InvalidOperationException>(query);
        }

        /// <summary>
        /// A rule definition whose first scoped read creates a mirror counts as a table definition: from another thread
        /// while the writable root is in use it throws <see cref="InvalidOperationException"/> and registers nothing;
        /// once the root is frozen, any thread may make one.
        /// </summary>
        [Fact]
        public void AFirstScopedReadFromAnotherThreadWhileTheWritableRootRunsIsRefused()
        {
            using var run = new BlockingRun();
            var tag = run.Graph.DefineScopeTag("outer");
            var fact = run.Graph.DefineRelation<string>("fact");
            var seen = run.Graph.DefineRelation<string>("seen");
            run.Start();

            var refused = Record.Exception(() => run.Rules.DefineRule("scoped", r =>
            {
                var x = r.Var<string>("x");
                r.When(fact.In(tag).Of(x));
                r.Then(seen.Of(x));
            }));

            run.Finish();
            Assert.IsType<InvalidOperationException>(refused);
            Assert.Empty(run.Graph.Mirrors.AllCopyRules());
            Assert.DoesNotContain(run.Rules.Rules, r => r.Name == "scoped");

            // Once the root is frozen, any thread.
            run.Graph.Root.Freeze();
            var defined = Task.Factory.StartNew(() => run.Rules.DefineRule("scoped", r =>
            {
                var x = r.Var<string>("x");
                r.When(fact.In(tag).Of(x));
                r.Then(seen.Of(x));
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
            Assert.Equal(new[] { tag }, defined.ReadScopes);
        }

    }

}
