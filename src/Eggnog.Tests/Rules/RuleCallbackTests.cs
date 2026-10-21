using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// Rule callbacks (<see cref="RuleBuilder.ThenCall"/>, <see cref="RuleBuilder.ThenCallFull"/>), egglog's
    /// <c>rust_rule</c> and <c>rust_rule_full</c>: what a callback sees and writes, reads from a full callback in a naive
    /// rule, terms and unions, deletions and the scope's mirrors, the writes refused when made, failures, and a callback's
    /// rule under a scheduler.
    /// </summary>
    public sealed class RuleCallbackTests
    {

        /// <summary>
        /// A callback sees the variables the body binds, a class as a usable sort value, and makes terms and unions them.
        /// </summary>
        [Fact]
        public void ACallbackMakesTermsAndUnionsThem()
        {
            var t = new NodeTheory();
            var rules = t.Rules;
            rules.DefineRule("wrap", rule =>
            {
                var (n, x) = rule.Vars<Node, long>("n", "x");
                rule.When(n.Is(t.Lit.Of(x)));
                rule.ThenCall(ctx =>
                {
                    var made = ctx.Make<Node>(t.Lit, ctx.Get(x) + 100);
                    ctx.Union(ctx.Get(n), made);
                    ctx.Insert(t.Tag, made);
                });
            });
            var one = t.Root.Make(t.Lit.Of(1L));

            t.Root.Run(rules);

            Assert.True(t.Root.AreEqual(one, t.Lit.Of(101L)));
            Assert.True(t.Root.Check(t.Tag.Of(t.Lit.Of(101L))));
        }

        /// <summary>
        /// A full callback reads tables, and its rule is naive: it finds a match again when the row it read changes.
        /// </summary>
        [Fact]
        public void AFullCallbackReadsInANaiveRule()
        {
            var graph = new EGraph();
            var key = graph.DefineRelation<long>("key");
            var best = graph.DefineFunction<long, long>("best", ValueLattice.Max<long>());
            var seen = graph.DefineFunction<long, long>("seen", ValueLattice.Max<long>());
            var rules = graph.DefineRuleset("rules");
            var rule = rules.DefineRule("copy", rule =>
            {
                var k = rule.Var<long>("k");
                rule.When(key.Of(k));
                rule.ThenCallFull(ctx =>
                {
                    Assert.False(ctx.Contains(key, ctx.Get(k) + 1000));
                    Assert.True(ctx.TableSize(key) > 0);
                    if (ctx.TryLookup<long>(best, [ctx.Get(k)], out var value))
                        ctx.Set(seen, [ctx.Get(k)], value);
                });
            });
            graph.Root.Assume(key.Of(1L), best.Of(1L).Is(3L));
            graph.Root.Run(rules);
            graph.Root.Assume(best.Of(1L).Is(7L));

            graph.Root.Run(rules);

            Assert.True(rule.IsNaive);
            Assert.Equal(7L, graph.Root.Get(seen.Of(1L)));
        }

        /// <summary>
        /// A callback's deletion keeps the scope's own mirror equal to its table, as a rule's deletion does.
        /// </summary>
        [Fact]
        public void ACallbacksDeletionReconcilesTheScopesMirror()
        {
            var t = new NodeTheory();
            var seen = t.Graph.DefineQuery("seen", q => q.When(t.Num.In(t.Graph.RootTag).Of(q.Var<long>("x"))));
            var rules = t.Rules;
            rules.DefineRule("drop two", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(t.Num.Of(x));
                rule.ThenCall(ctx =>
                {
                    if (ctx.Get(x) == 2L)
                        ctx.Remove(t.Num, 2L);
                });
            });
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L));
            Assert.Equal(3, t.Root.Count(seen));

            t.Root.Run(rules);

            Assert.Equal(2, t.Root.Count(seen));
            Assert.Equal(2, t.Root.CountRows(t.Num));
        }

        /// <summary>
        /// The writes a head would refuse before the run are refused when a callback makes them, failing the rule: a
        /// function whose merge is not a lattice below the root, a deletion where the scope may not delete, a subsumption
        /// of a table that is not subsumable, an analysis table, and a variable not bound before the callback.
        /// </summary>
        [Fact]
        public void TheWritesAHeadMayNotMakeAreRefused()
        {
            var t = new NodeTheory();
            var sum = t.Graph.DefineFunction<long, long>("sum", FunctionMerge.Of<long>("sum", (a, b) => a + b));
            var plain = t.Graph.DefineRelation<long>("plain", new TableOptions { Subsumable = false });
            var analysis = t.Graph.DefineRelation<long>("analysis", new TableOptions { Role = TableRole.Analysis });
            t.Root.Assume(t.Num.Of(1L), plain.Of(1L));

            Assert.Contains("only the root writes it", Fail(t, t.Root.CreateChild(), (ctx, x) => ctx.Set(sum, [ctx.Get(x)], 1L)));
            var parent = t.Root.CreateChild();
            parent.CreateChild();
            Assert.Contains("cannot lose a row", Fail(t, parent, (ctx, x) => ctx.Remove(t.Num, ctx.Get(x))));
            Assert.Contains("not subsumable", Fail(t, t.Root.CreateChild(), (ctx, x) => ctx.Subsume(plain, ctx.Get(x))));
            Assert.Contains("analysis table", Fail(t, t.Root.CreateChild(), (ctx, x) => ctx.Insert(analysis, ctx.Get(x))));

            var unbound = t.Graph.DefineRuleset("unbound");
            unbound.DefineRule("unbound", rule =>
            {
                var (x, y) = rule.Vars<long, long>("x", "y");
                rule.When(t.Num.Of(x));
                rule.ThenCall(ctx => ctx.Insert(t.Num, ctx.Get(y)));
            });
            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.CreateChild().Run(unbound));
            Assert.Contains("is not bound before the callback", e.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// An exception a callback throws fails the rule with its bindings; its context answers nothing after the call;
        /// and a callback cannot call into a graph.
        /// </summary>
        [Fact]
        public void ACallbacksFailureFailsTheRule()
        {
            var t = new NodeTheory();
            RuleCallbackContext? kept = null;
            var rules = t.Rules;
            rules.DefineRule("keep", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(t.Num.Of(x));
                rule.ThenCall(ctx => kept = ctx);
            });
            t.Root.Assume(t.Num.Of(4L));
            t.Root.Run(rules);
            Assert.Throws<InvalidOperationException>(() => kept!.Insert(t.Num, 5L));

            var child = t.Root.CreateChild();
            var e = Assert.Throws<RuleEvaluationException>(() => child.Run(Rules(t, (_, _) => throw new FormatException("bad"))));
            Assert.Contains("callback threw FormatException: bad", e.Message, StringComparison.Ordinal);
            Assert.Contains("x=4", e.Bindings, StringComparison.Ordinal);

            var other = t.Root.CreateChild();
            var addScheduler = Rules(t, (_, _) => t.Graph.AddScheduler(new FirstNScheduler(1)));
            var call = Assert.Throws<RuleEvaluationException>(() => other.Run(addScheduler));
            Assert.IsType<InvalidOperationException>(call.InnerException);
        }

        /// <summary>
        /// A rule's callback renders as egglog's <c>rust_rule</c> head, a call with the rule's variables.
        /// </summary>
        [Fact]
        public void ACallbackRendersAsACall()
        {
            var t = new NodeTheory();
            var rules = t.Rules;
            var rule = rules.DefineRule("call", rule =>
            {
                var (x, y) = rule.Vars<long, long>("x", "y");
                rule.When(t.Num.Of(x), t.Num.Of(y));
                rule.ThenCall(_ => { });
            });

            Assert.Contains("((<callback> x y))", rule.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// A scheduler steps a rule whose head is a callback: a match holds the variables the callback sees.
        /// </summary>
        [Fact]
        public void ASchedulerStepsACallbacksRule()
        {
            var t = new NodeTheory();
            var copied = t.Graph.DefineRelation<long>("copied");
            var rules = t.Rules;
            rules.DefineRule("copy", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(t.Num.Of(x));
                rule.ThenCall(ctx => ctx.Insert(copied, ctx.Get(x)));
            });
            for (var i = 0L; i < 5; i++)
                t.Root.Assume(t.Num.Of(i));

            var first = new FirstNScheduler(2);
            var scheduler = t.Graph.AddScheduler(first);
            t.Root.Run(Schedule.RunWith(scheduler, rules));

            Assert.Equal(2, t.Root.CountRows(copied));
            Assert.Equal(5, first.Offered[0]);
        }

        /// <summary>
        /// Callbacks run on a parallel run's worker threads, which do not hold the scope's thread domain: their classes are
        /// canonical through the run's own find, at the root and in a child.
        /// </summary>
        [Fact]
        public void CallbacksRunOnAParallelRunsWorkers()
        {
            var t = new NodeTheory(new EGraphOptions { DegreeOfParallelism = 4 });
            t.Rules.DefineRule("wrap", rule =>
            {
                var (n, x) = rule.Vars<Node, long>("n", "x");
                rule.When(n.Is(t.Lit.Of(x)));
                rule.ThenCall(ctx =>
                {
                    var made = ctx.Make<Node>(t.Lit, ctx.Get(x) + 1_000_000);
                    ctx.Union(ctx.Get(n), made);
                    ctx.Insert(t.Tag, made);
                });
            });
            for (var i = 0L; i < 12_000; i++)
                t.Root.Make(t.Lit.Of(i));

            t.Root.Engine.Context.Context.Database.ParallelRuleSetsOverride = true;
            t.Root.Run(t.Rules);
            Assert.Equal(12_000, t.Root.CountRows(t.Tag));

            t.Root.Freeze();
            var child = t.Root.CreateChild(null, new ScopeOptions { DegreeOfParallelism = 4 });
            child.Engine.Context.Context.Database.ParallelRuleSetsOverride = true;
            // Clear of the root's terms and of what the rule makes of them, two millions up.
            for (var i = 5_000_000L; i < 5_012_000; i++)
                child.Make(t.Lit.Of(i));

            child.Run(t.Rules);
            Assert.Equal(24_000, child.CountRows(t.Tag));
        }

        /// <summary>
        /// A full callback reads outside a licence, which sees only atoms: a licensed rule must call ReadsOwnFacts.
        /// </summary>
        [Fact]
        public void AFullCallbackNeedsReadsOwnFactsUnderALicence()
        {
            var t = new NodeTheory();
            var tag = t.Graph.DefineScopeTag("schema");
            var licensed = t.Graph.DefineRuleset("licensed", new RulesetOptions { Licence = tag });

            var e = Assert.Throws<RuleDefinitionException>(() => licensed.DefineRule("read", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(t.Num.In(tag).Of(x));
                rule.ThenCallFull(ctx => ctx.Contains(t.Num, ctx.Get(x)));
            }));
            Assert.Contains("full callback", e.Message, StringComparison.Ordinal);

            licensed.DefineRule("own", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(t.Num.In(tag).Of(x));
                rule.ReadsOwnFacts("the callback reads only rows the schema holds");
                rule.ThenCallFull(ctx => ctx.Contains(t.Num, ctx.Get(x)));
            });
        }

        /// <summary>
        /// A refusal fails the rule even when the callback catches it.
        /// </summary>
        [Fact]
        public void ACaughtRefusalStillFailsTheRule()
        {
            var t = new NodeTheory();
            var plain = t.Graph.DefineRelation<long>("plain", new TableOptions { Subsumable = false });
            t.Root.Assume(t.Num.Of(1L), plain.Of(1L));

            var message = Fail(t, t.Root.CreateChild(), (ctx, x) =>
            {
                try
                {
                    ctx.Subsume(plain, ctx.Get(x));
                }
                catch (ArgumentException)
                {
                }
            });

            Assert.Contains("not subsumable", message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Runs a one-rule ruleset whose callback fails in <paramref name="scope"/>, returning the failure's message.
        /// </summary>
        static string Fail(NodeTheory t, Scope scope, Action<RuleCallbackContext, Var<long>> callback) =>
            Assert.Throws<RuleEvaluationException>(() => scope.Run(Rules(t, callback))).Message;

        /// <summary>
        /// A ruleset of one rule over <c>Num(x)</c> whose head is <paramref name="callback"/>.
        /// </summary>
        static Ruleset Rules(NodeTheory t, Action<RuleCallbackContext, Var<long>> callback)
        {
            var rules = t.Graph.DefineRuleset("rules" + Guid.NewGuid().ToString("N"));
            rules.DefineRule("call", rule =>
            {
                var x = rule.Var<long>("x");
                rule.When(t.Num.Of(x));
                rule.ThenCall(ctx => callback(ctx, x));
            });
            return rules;
        }

    }

}
