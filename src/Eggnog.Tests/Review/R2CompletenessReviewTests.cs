using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Probes of R2 against the rule-layer specification (completeness review): behaviour the spec states for R2 that no
    /// R2 test pinned. Five fail at the reviewed commit (924c111): P01 (CreateChild inside a run observer is allowed), P02
    /// and P03 (a query's equated class constants are compared at the root, not in the scope it runs in), P04 (a rule
    /// definition with equated class constants from another thread while the writable root runs gets the engine's domain
    /// error), and P09 (four seeds' seeded match totals exceed their unseeded ones; the differential asserts only the sum).
    /// </summary>
    public sealed class R2CompletenessReviewTests
    {

        /// <summary>
        /// §6.11, §6.13: a write, run or structural call from a run observer throws InvalidOperationException; only reads
        /// are allowed there. CreateChild is a structural call.
        /// </summary>
        [Fact]
        public void P01_CreateChildFromARunObserverIsRefused()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            var scope = t.Root.CreateChild();
            scope.Assume(t.Fact.Of("a"));

            Exception? refused = null;
            var created = false;
            scope.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = _ =>
                {
                    if (refused is not null || created)
                        return;

                    refused = Record.Exception(() => scope.CreateChild());
                    created = refused is null;
                },
            });

            Assert.IsType<InvalidOperationException>(refused);
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// D39, §6.3 step 3, §6.6.4: a query may hold classes of any scope; its origin checks run at each call, against the
        /// scope it runs in. Two class constants that are one class in that scope must match there, even when the root
        /// never saw them unioned.
        /// </summary>
        [Fact]
        public void P02_AQuerysEquatedClassConstantsCompareInTheScopeItRunsIn()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            schema.Union(a, b);
            schema.Assume(t.Num.Of(1L));

            var query = t.Graph.DefineQuery("equated in the child", q =>
            {
                q.When(t.Num.Of(q.Var<long>("n")), ((Term<Node>)a).Is(b));
            });

            Assert.Equal(1, schema.Count(query));
        }

        /// <summary>
        /// The same with classes minted in the child: the root has never seen either id.
        /// </summary>
        [Fact]
        public void P03_AQuerysEquatedClassConstantsMintedInAChildCompareThere()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            var a = schema.Make(t.A.Of());
            var b = schema.Make(t.B.Of());
            schema.Union(a, b);
            schema.Assume(t.Num.Of(1L));

            var query = t.Graph.DefineQuery("minted in the child", q =>
            {
                q.When(t.Num.Of(q.Var<long>("n")), ((Term<Node>)a).Is(b));
            });

            Assert.Equal(1, schema.Count(query));
        }

        /// <summary>
        /// D6, §6.12: rule definitions may be made from any thread at any time and join the next iteration. A global rule
        /// whose two class constants are one class at the root, defined while the writable root runs on another thread.
        /// </summary>
        [Fact]
        public void P04_AGlobalRuleWithEquatedClassConstantsMayBeDefinedWhileTheRootRuns()
        {
            using var run = new BlockingRun();
            run.Graph.DefineSort<Node>("Node");
            var a = run.Graph.DefineConstructor<Node>("A");
            var b = run.Graph.DefineConstructor<Node>("B");
            var num = run.Graph.DefineRelation<long>("num");
            var ca = run.Graph.Root.Make(a.Of());
            var cb = run.Graph.Root.Make(b.Of());
            run.Graph.Root.Union(ca, cb);
            run.Graph.Root.Assume(num.Of(1L));
            run.Start();

            var defined = Record.Exception(() => run.Rules.DefineRule("equated", r =>
            {
                r.When(num.Of(r.Var<long>("n")), ((Term<Node>)ca).Is(cb));
                r.Then(run.Done.Of(2L));
            }));

            run.Finish();
            Assert.Null(defined);
        }

        /// <summary>
        /// D6, §6.12, B11: a rule definition whose first scoped read creates a mirror counts as a table definition: from
        /// another thread while the writable root is in use it throws InvalidOperationException and registers nothing;
        /// once the root is frozen, any thread may make one.
        /// </summary>
        [Fact]
        public void P05_AFirstScopedReadFromAnotherThreadWhileTheWritableRootRunsIsRefused()
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

        /// <summary>
        /// §6.3 step 7, invariant A: a rule that writes an analysis table may read no scoped atom.
        /// </summary>
        [Fact]
        public void P06_AnAnalysisWriterReadingAScopedAtomIsRefused()
        {
            var t = new ScopeTheory();
            var source = t.Graph.DefineRelation<string>("source", new TableOptions { Role = TableRole.Analysis });
            var analysis = t.Graph.DefineRelation<string>("analysis", new TableOptions { Role = TableRole.Analysis });
            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("analysis from scoped", r =>
            {
                var x = r.Var<string>("x");
                r.When(source.In(t.Outer).Of(x));
                r.Then(analysis.Of(x));
            }));
            Assert.Equal("analysis from scoped", e.RuleName);
            Assert.Equal("rule writes analysis table 'analysis' and may not read scoped atoms; it reads 'source@outer'", e.Message);
            Assert.Empty(t.Rules.Rules);
        }

        /// <summary>
        /// D45's table (identity members answer as usual) and D40: the identity and tree members of a disposed, released
        /// scope answer; only use throws.
        /// </summary>
        [Fact]
        public void P07_TheIdentityMembersOfAReleasedScopeAnswer()
        {
            var t = new ScopeTheory();
            var child = t.Root.CreateChild(t.Outer);
            child.Assume(t.Fact.Of("a"));
            child.Dispose();
            Assert.True(child.Engine.IsDisposed);

            var answers = new List<Exception?>
            {
                Record.Exception(() => _ = child.IsFrozen),
                Record.Exception(() => _ = child.Children),
                Record.Exception(() => _ = child.LocalRules),
                Record.Exception(() => _ = child.Tag),
                Record.Exception(() => _ = child.Parent),
                Record.Exception(() => _ = child.IsFaulted),
                Record.Exception(() => _ = child.Failure),
                Record.Exception(() => _ = child.Path),
            };

            Assert.All(answers, Assert.Null);
        }

        /// <summary>
        /// D48, invariant M (and the r2-impl deviation that records the copy-rule count): a mirror created after the
        /// tagged scope's last sync is synced before it is first read, although the scope did not change.
        /// </summary>
        [Fact]
        public void P08_AMirrorCreatedAfterASyncIsSyncedBeforeItsFirstRead()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"), t.Seen.Of("s"));
            var inner = outer.CreateChild(t.Inner);
            inner.Run(Schedule.Saturate(t.Rules));
            Assert.True(inner.Check(copies.Facts.Of("a")));
            var version = outer.Version;

            var seenCopies = t.Graph.DefineRelation<string>("seen copies");
            t.Rules.DefineRule("copy seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.In(t.Outer).Of(x));
                r.Then(seenCopies.Of(x));
            });
            var seen = t.Graph.DefineQuery("seen outer", q => q.When(t.Seen.In(t.Outer).Of(q.Var<string>("x"))));

            Assert.Equal(version, outer.Version);
            Assert.Equal(1, inner.Count(seen));
            inner.Run(Schedule.Saturate(t.Rules));
            Assert.True(inner.Check(seenCopies.Of("s")));
        }

        /// <summary>
        /// §3.8 NodeView, §6.6.7: a class a per-row cost function reads carries the extractor scope's origin, so a class
        /// read while extracting in a child is refused at the root.
        /// </summary>
        [Fact]
        public void P10_ANodeViewClassReadInAChildCarriesTheChildsOrigin()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var child = t.Root.CreateChild();
            var wrapped = child.Make(t.Wrap.Of(t.A.Of()));
            var seen = new List<Node>();
            var costs = CostModel.Default.With(t.Wrap, node =>
            {
                lock (seen)
                    seen.Add(node.Get<Node>(0));

                return 1;
            });

            Assert.Equal("(Wrap (A))", child.Extract(wrapped, costs).ToString());
            Assert.NotEmpty(seen);
            Assert.All(seen, n => Assert.Throws<ArgumentException>(() => t.Root.Find(n)));
            Assert.All(seen, n => Assert.Equal(child.Find(child.Make(t.A.Of())), child.Find(n)));
        }

        /// <summary>
        /// §3.1 Version and §6.6.4 ("Version does not move" for a query in a writable scope) against D48 (a query syncs
        /// the mirrors it reads, which writes the mirror rows into a writable tagged scope). The mirror tables are internal,
        /// so their sync moves neither the tagged scope's version nor any extractor (they are not extraction tables, E5).
        /// </summary>
        [Fact]
        public void P11_AScopedQueryInAWritableTaggedScopeMovesNeitherItsVersionNorAnExtractor()
        {
            var t = new ScopeTheory();
            t.Graph.DefineSort<Node>("Node");
            var a = t.Graph.DefineConstructor<Node>("A");
            var query = t.Graph.DefineQuery("outer facts", q => q.When(t.Fact.In(t.Outer).Of(q.Var<string>("x"))));
            var outer = t.Root.CreateChild(t.Outer);
            var term = outer.Make(a.Of());
            outer.Assume(t.Fact.Of("a"));
            var extractor = outer.CreateExtractor();
            var version = outer.Version;

            Assert.Equal(1, outer.Count(query));

            Assert.Equal(version, outer.Version);
            Assert.Equal("(A)", extractor.Extract(term).ToString());

            // Once synced, a second query moves nothing.
            version = outer.Version;
            Assert.Equal(1, outer.Count(query));
            Assert.Equal(version, outer.Version);
        }

        /// <summary>
        /// D31, §4.1 ScopeRuntime.Of: a lattice that throws during a rule's merge in a child is recorded in the child's
        /// runtime, so the child's exception carries it, as LatticeTests checks at the root.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void P12_ALatticeThatThrowsDuringAChildsMergeIsReportedThere(bool frozenRoot)
        {
            var graph = new EGraph();
            var boom = new InvalidOperationException("boom");
            var f = graph.DefineFunction<string, long>("f", new ValueLattice<long>("picky", (a, b) => a == b ? a : throw boom));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("write two", r => r.Then(f.Of("k").Is(2L)));
            if (frozenRoot)
                graph.Root.Freeze();

            var child = graph.Root.CreateChild();
            child.Assume(f.Of("k").Is(1L));

            var e = Assert.Throws<RuleEvaluationException>(() => child.Run(rules));

            Assert.Null(e.RuleName);
            Assert.Same(boom, e.InnerException);
            Assert.Contains("picky", e.Message);
            Assert.True(child.IsFaulted);
            Assert.False(graph.Root.IsFaulted);
        }

        /// <summary>
        /// §6.4.3: a local rule's variant is described <c>{scope path}:{ruleset}/{name}#{serial}</c> and a mirror copy rule's
        /// <c>$mirror/{mirror name}#{serial}</c>; copy rules are never listed in a run's report (§6.5.4).
        /// </summary>
        [Fact]
        public void P13_LocalAndCopyRulesAreDescribedAsSpecified()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var outer = t.Root.CreateChild(t.Outer);
            var local = outer.DefineRule(t.Rules, "local", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            Assert.Equal($"{outer.Path}:rules/local#{local.Compiled.Serial}", local.Compiled.Variants.Default!.Description);

            var mirror = t.Graph.Mirrors.Get(t.Fact, t.Outer);
            var copy = mirror.CopyRule;
            Assert.Equal($"$mirror/fact@outer#{copy.Serial}", copy.Variants.Default!.Description);

            outer.Assume(t.Fact.Of("a"));
            var report = outer.Run(Schedule.Saturate(t.Rules));
            Assert.True(outer.Check(copies.Facts.Of("a"), t.Seen.Of("a")));
            Assert.DoesNotContain(copy.Rule, report.Rules.Keys);
            Assert.DoesNotContain(report.Rules.Keys, r => r.Ruleset.Name == "$mirror");
            Assert.Contains(local, report.Rules.Keys);
        }

        /// <summary>
        /// D40, §8.3 DisposeTests: a frozen schema disposed while 8 queries run beneath it on other threads is released
        /// after the last query, not before: the queries end one at a time.
        /// </summary>
        [Fact]
        public void P14_AFrozenSchemaIsReleasedOnlyAfterItsLastQueryEnds()
        {
            const int Queries = 8;
            var t = new ScopeTheory();
            var entered = new CountdownEvent(Queries);
            var gates = new ManualResetEventSlim[Queries];
            for (var i = 0; i < Queries; i++)
                gates[i] = new ManualResetEventSlim();

            t.Rules.DefineRule("wait", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Where(x, v =>
                {
                    if (v.StartsWith('q'))
                    {
                        entered.Signal();
                        gates[v[1] - '0'].Wait(TimeSpan.FromSeconds(30));
                    }

                    return true;
                });
                r.Then(t.Seen.Of(x));
            });
            t.Root.Freeze();
            var schema = t.Root.CreateChild(t.Outer);
            var local = schema.DefineRule(t.Rules, "local", r => r.When(t.Seen.Of(r.Var<string>("x"))));
            schema.Freeze();
            var description = local.Compiled.Variants.Default!.Description;

            var runs = new Task[Queries];
            for (var i = 0; i < Queries; i++)
            {
                var value = "q" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                runs[i] = Task.Factory.StartNew(() =>
                {
                    using var query = schema.CreateChild();
                    query.Assume(t.Fact.Of(value));
                    query.Run(Schedule.Saturate(t.Rules));
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            schema.Dispose();
            for (var i = 0; i < Queries - 1; i++)
            {
                gates[i].Set();
                runs[i].GetAwaiter().GetResult();
                Assert.False(schema.Engine.IsDisposed);
                Assert.True(t.Graph.VariantsByDescription.ContainsKey(description));
            }

            gates[Queries - 1].Set();
            runs[Queries - 1].GetAwaiter().GetResult();
            Assert.True(schema.Engine.IsDisposed);
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(description));
            Assert.Empty(t.Root.Children);
            foreach (var gate in gates)
                gate.Dispose();

            entered.Dispose();
        }

        /// <summary>
        /// §8.2: InheritanceOffEquivalenceTests' "the seeded match totals must be at most the unseeded ones", per program
        /// (the test as built asserts only the sum over all seeds). Lists the seeds whose seeded total exceeds the
        /// unseeded one.
        /// </summary>
        [Fact]
        public void P09_EverySeedsSeededMatchTotalIsAtMostItsUnseededOne()
        {
            var over = new List<string>();
            for (var seed = 0; seed < 300; seed++)
            {
                var program = Differential.ScopedProgram.Generate(seed, true);
                var on = new Differential.ScopedDriver(program, inheritWatermarks: true);
                var off = new Differential.ScopedDriver(program, inheritWatermarks: false);
                foreach (var command in program.Commands)
                {
                    on.Execute(command, named: true);
                    off.Execute(command, named: true);
                }

                if (on.Matches > off.Matches)
                    over.Add($"seed {seed}: seeded {on.Matches}, unseeded {off.Matches}");
            }

            Assert.True(over.Count == 0, string.Join("\n", over));
        }

    }

}
