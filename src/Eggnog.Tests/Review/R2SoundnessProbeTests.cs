using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Probes of R2's soundness (review): mirrors and their syncs (D48, invariant M), queries on writable tagged scopes,
    /// failed definitions, and evaluation failures in children. Each probe states what the spec requires.
    /// </summary>
    public sealed class R2SoundnessProbeTests
    {

        /// <summary>
        /// Invariant M with a nested scope of the same tag: a later write into the writable outer scope reaches the inner
        /// scope's view of the lattice function and relation, and the inner scope's mirror follows it.
        /// </summary>
        [Fact]
        public void P01_ANestedScopeOfTheSameTagFollowsItsParentsLaterWrites()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var a = t.Root.CreateChild(t.Outer);
            a.Assume(t.Fact.Of("x"), t.Best.Of("k").Is(5L));
            a.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(5L, a.Get(copies.Bests.Of("k")));

            var b = a.CreateChild(t.Outer);
            b.Assume(t.Best.Of("k").Is(9L));
            b.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(9L, b.Get(copies.Bests.Of("k")));

            a.Assume(t.Fact.Of("y"), t.Best.Of("k").Is(20L));
            Assert.Equal(20L, b.Get(t.Best.Of("k")));
            Assert.Equal(20L, b.Get(t.Best.In(t.Outer).Of("k")));

            b.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(20L, b.Get(copies.Bests.Of("k")));
            Assert.True(b.Check(copies.Facts.Of("y")));
        }

        /// <summary>
        /// A mirror created after a scope's last sync is synced at its next run, even though the scope did not change
        /// (the copy-rule count of the sync stamp).
        /// </summary>
        [Fact]
        public void P02_AMirrorCreatedAfterASyncIsSyncedAtTheNextRun()
        {
            var t = new ScopeTheory();
            var facts = t.Graph.DefineRelation<string>("outer facts");
            t.Rules.DefineRule("copy facts", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Outer).Of(x));
                r.Then(facts.Of(x));
            });
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"), t.Best.Of("k").Is(3L));
            var inner = outer.CreateChild(t.Inner);
            inner.Run(Schedule.Saturate(t.Rules));
            Assert.True(inner.Check(facts.Of("a")));

            var bests = t.Graph.DefineFunction<string, long>("outer bests", ValueLattice.Max<long>());
            t.Rules.DefineRule("copy bests", r =>
            {
                var (k, v) = r.Vars<string, long>("k", "v");
                r.When(v.Is(t.Best.In(t.Outer).Of(k)));
                r.Then(bests.Of(k).Is(v));
            });
            inner.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(3L, inner.Get(bests.Of("k")));
        }

        /// <summary>
        /// §8.3 QueryTests ("Version unchanged by queries; an extractor created before Count, Any or ForEachMatch still
        /// extracts after it") and §6.10 ("reads, queries and writes that change nothing never invalidate it"): a query
        /// with a scoped atom on a writable tagged scope.
        /// </summary>
        [Fact]
        public void P03_AScopedQueryOnAWritableTaggedScopeKeepsItsVersionAndExtractor()
        {
            var t = new NodeTheory();
            var outerTag = t.Graph.DefineScopeTag("outer");
            var nums = t.Graph.DefineQuery("outer nums", q => q.When(t.Num.In(outerTag).Of(q.Var<long>("n"))));
            var one = t.Root.Make(t.Lit.Of(1L));
            var outer = t.Root.CreateChild(outerTag);
            outer.Assume(t.Num.Of(5L));
            var extractor = outer.CreateExtractor();
            var version = outer.Version;

            Assert.Equal(1, outer.Count(nums));

            Assert.Equal("(Lit 1)", extractor.Extract(one).ToString());
            Assert.Equal(version, outer.Version);
        }

        /// <summary>
        /// The same from a descendant: a query whose scoped atom resolves to the writable tagged parent must not make the
        /// descendant's extractor stale (§6.10, ContextExtractionTests: "a query in the child does not").
        /// </summary>
        [Fact]
        public void P04_AScopedQueryInADescendantKeepsTheDescendantsExtractor()
        {
            var t = new NodeTheory();
            var outerTag = t.Graph.DefineScopeTag("outer");
            var nums = t.Graph.DefineQuery("outer nums", q => q.When(t.Num.In(outerTag).Of(q.Var<long>("n"))));
            var one = t.Root.Make(t.Lit.Of(1L));
            var outer = t.Root.CreateChild(outerTag);
            outer.Assume(t.Num.Of(5L));
            var inner = outer.CreateChild();
            var extractor = inner.CreateExtractor();

            Assert.Equal(1, inner.Count(nums));

            Assert.Equal("(Lit 1)", extractor.Extract(one).ToString());
        }

        /// <summary>
        /// §6.13: "An unresolved tag at Run or at a query: InvalidOperationException, before any work". A query reading
        /// two tags, the first resolving to the writable scope and stale, the second unresolved.
        /// </summary>
        [Fact]
        public void P05_AQueryWithAnUnresolvedTagIsRefusedBeforeAnyWork()
        {
            var t = new ScopeTheory();
            var both = t.Graph.DefineQuery("both", q =>
            {
                var x = q.Var<string>("x");
                q.When(t.Fact.In(t.Outer).Of(x), t.Fact.In(t.Inner).Of(x));
            });
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));
            var version = outer.Version;

            Assert.Throws<InvalidOperationException>(() => outer.Count(both));

            Assert.Equal(version, outer.Version);
        }

        /// <summary>
        /// §6.3: a definition that fails registers nothing (RuleDefinitionTests: "A failed definition registers
        /// nothing"). A rule whose body reads a scoped atom and whose head uses an unbound variable.
        /// </summary>
        [Fact]
        public void P06_AFailedDefinitionWithAScopedReadRegistersNoMirror()
        {
            var t = new ScopeTheory();
            var variants = t.Graph.VariantsByDescription.Count;
            Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("unbound head", r =>
            {
                var (x, y) = r.Vars<string, string>("x", "y");
                r.When(t.Fact.In(t.Outer).Of(x));
                r.Then(t.Seen.Of(y));
            }));
            Assert.Throws<RuleDefinitionException>(() => t.Graph.DefineQuery("unbound query", q =>
            {
                var k = q.Var<string>("k");
                q.When(t.Best.In(t.Inner).Of("k").Is(5L));
                q.Where(k, _ => true);
            }));

            Assert.Empty(t.Graph.Mirrors.AllCopyRules());
            Assert.Equal(variants, t.Graph.VariantsByDescription.Count);
        }

        /// <summary>
        /// The consequence of P06 on the frozen-tag refusal (§6.3 step 2): after only a failed definition read
        /// <c>fact@outer</c>, freezing a scope tagged outer must still refuse the first real reader.
        /// </summary>
        [Fact]
        public void P07_AFailedDefinitionDoesNotCountAsTheFirstScopedRead()
        {
            var t = new ScopeTheory();
            Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("unbound head", r =>
            {
                var (x, y) = r.Vars<string, string>("x", "y");
                r.When(t.Fact.In(t.Outer).Of(x));
                r.Then(t.Seen.Of(y));
            }));
            t.Root.Freeze();
            var outer = t.Root.CreateChild(t.Outer);
            outer.Freeze();

            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("late reader", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Outer).Of(x));
                r.Then(t.Seen.Of(x));
            }));
            Assert.Contains("first read in scope 'outer'", e.Message);
        }

        /// <summary>
        /// LatticeTests' contract (a lattice that throws during a rule's merge gives RuleEvaluationException whose inner
        /// exception is the delegate's), in a child scope: the failure must be recorded on the child's runtime.
        /// </summary>
        [Fact]
        public void P08_ALatticeThatThrowsInAChildsRunKeepsItsCause()
        {
            var graph = new EGraph();
            var boom = new InvalidOperationException("boom");
            var f = graph.DefineFunction<string, long>("f", new ValueLattice<long>("picky", (a, b) => a == b ? a : throw boom));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("write two", r => r.Then(f.Of("k").Is(2L)));
            graph.Root.Assume(f.Of("k").Is(1L));
            graph.Root.Freeze();
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<RuleEvaluationException>(() => child.Run(rules));

            Assert.Same(boom, e.InnerException);
            Assert.Contains("picky", e.Message);
            Assert.True(child.IsFaulted);
            Assert.False(graph.Root.IsFaulted);
        }

        /// <summary>
        /// The same for a host write into a child (LatticeTests: "a lattice that throws while a host write is checked
        /// writes nothing and does not fault").
        /// </summary>
        [Fact]
        public void P09_ALatticeThatThrowsInAChildsHostWriteKeepsItsCause()
        {
            var graph = new EGraph();
            var boom = new InvalidOperationException("boom");
            var f = graph.DefineFunction<string, long>("f", new ValueLattice<long>("picky", (a, b) => a == b ? a : throw boom));
            graph.Root.Assume(f.Of("k").Is(1L));
            var child = graph.Root.CreateChild();

            var e = Assert.Throws<RuleEvaluationException>(() => child.Assume(f.Of("k").Is(2L)));

            Assert.Same(boom, e.InnerException);
            Assert.Contains("picky", e.Message);
            Assert.Equal(1L, child.Get(f.Of("k")));
        }

        /// <summary>
        /// §6.12: rule definitions are allowed from any thread at any time (D6), and §6.3 evaluates <c>c1.Is(c2)</c> of a
        /// global rule's class constants in the root when the rule is defined. A definition from another thread while the
        /// writable root is inside an engine call must still give the definition's own answer, never the engine's domain
        /// error: the root cannot be read then (D47), so the comparison is left to run time (§6.3 step 3), where the two
        /// distinct classes never match.
        /// </summary>
        [Fact]
        public async Task P10_AGlobalRuleWithEquatedClassConstantsDefinedDuringARunGetsItsOwnAnswer()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            t.Rules.DefineRule("block", r =>
            {
                var n = r.Var<long>("n");
                r.When(t.Num.Of(n));
                r.Where(n, _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return true;
                });
            });
            t.Root.Assume(t.Num.Of(1L));
            var run = Task.Factory.StartNew(() => t.Root.Run(t.Rules), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            Exception? thrown;
            try
            {
                thrown = Record.Exception(() => t.Rules.DefineRule("distinct", r =>
                {
                    var x = r.Var<Node>("x");
                    r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                    r.Then(t.Num.Of(7L));
                }));
            }
            finally
            {
                release.Set();
            }

            await run;
            Assert.False(thrown is InvalidOperationException, thrown?.Message);
            Assert.Null(thrown);

            // Left to run time, the comparison keeps no match: a and b are two classes at the root.
            t.Root.Assume(t.Tag.Of(a), t.Tag.Of(b));
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.Check(t.Num.Of(7L)));
        }

        /// <summary>
        /// D47 and colors §8.3: each child of a frozen scope is its own thread domain, and a scope's guard is reassigned
        /// only while no call of the domain is in progress. A child created on another thread in the window between the
        /// engine freeze and <c>Freeze</c>'s reassignment of the child guards (here held open by holding the registry's
        /// lock that <c>AddFrozen</c> takes) is already in use on its own thread when the freezing thread gives it a new
        /// guard: a third thread then enters the child's domain past the rule-layer guard, and the run that held the old
        /// guard fails when it exits a guard it never entered.
        /// </summary>
        [Fact]
        public void P11_AChildCreatedWhileItsParentFreezesKeepsItsThreadDomain()
        {
            var t = new ScopeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            t.DefineBlocking(entered, release);
            t.Root.Freeze();
            var parent = t.Root.CreateChild(t.Outer);
            var registryLock = typeof(MirrorRegistry).GetField("sync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(t.Graph.Mirrors)!;

            Task freeze;
            Scope? child = null;
            Task<RunReport>? run = null;
            Monitor.Enter(registryLock);
            try
            {
                freeze = Task.Factory.StartNew(parent.Freeze, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(SpinWait.SpinUntil(() => parent.IsFrozen, TimeSpan.FromSeconds(30)));

                // The engine froze the parent; Freeze waits in AddFrozen. Another thread creates a child and runs it.
                run = Task.Factory.StartNew(() =>
                {
                    child = parent.CreateChild();
                    child.Assume(t.Fact.Of("a"));
                    return child.Run(Schedule.Saturate(t.Rules));
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            }
            finally
            {
                Monitor.Exit(registryLock);
            }

            Assert.True(freeze.Wait(TimeSpan.FromSeconds(30)));

            // The child's domain is in use by the run: a second thread must get the rule layer's refusal.
            var intruder = Task.Factory.StartNew(() => Record.Exception(() => child!.Assume(t.Fact.Of("b"))),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
            release.Set();
            var finished = Record.Exception(() => run.GetAwaiter().GetResult());

            Assert.Contains("is in use on another thread", Assert.IsType<InvalidOperationException>(intruder).Message);
            Assert.Null(finished);
            var report = run.Result;
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(child!.Check(t.Seen.Of("a")));
        }

        /// <summary>
        /// Rule-layer §7.2 K (colors I30, X19) in a child: a rule's class constant that loses a union only in the child,
        /// after the child already ran the rule, still matches the rows the child had (the watermark is not used with
        /// changed constants). Both union directions, for global and local rules.
        /// </summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void P12_AConstantThatLosesAUnionOnlyInAChildStillMatchesOldRows(bool local, bool constantWins)
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            void Define(RuleBuilder r)
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a));
                r.Then(t.Num.Of(1L));
            }

            if (local)
                schema.DefineRule(t.Rules, "tagged a", Define);
            else
                t.Rules.DefineRule("tagged a", Define);

            schema.Freeze();
            var query = schema.CreateChild();
            query.Assume(t.Tag.Of(b));
            query.Run(Schedule.Saturate(t.Rules));
            Assert.False(query.Check(t.Num.Of(1L)));

            if (constantWins)
                query.Union(b, a);
            else
                query.Union(a, b);

            query.Run(Schedule.Saturate(t.Rules));
            Assert.True(query.Check(t.Num.Of(1L)));
        }

    }

}
