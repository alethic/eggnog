using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Predicates.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Scope tags belong to their graph: unique names, the reserved root tag, refusal of another graph's tag everywhere
    /// one is taken, and no state shared between graphs.
    /// </summary>
    public sealed class ScopeTagTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        [Fact]
        public void TagNamesAreUniquePerGraphAndRootIsTheRoots()
        {
            var graph = new EGraph();
            Assert.Equal("root", graph.RootTag.Name);
            Assert.Same(graph, graph.RootTag.Graph);
            Assert.Equal(new[] { graph.RootTag }, graph.ScopeTags);
            Assert.Same(graph.RootTag, graph.Root.Tag);

            var schema = graph.DefineScopeTag("schema");
            Assert.Equal("schema", schema.ToString());
            Assert.Equal(new[] { graph.RootTag, schema }, graph.ScopeTags);
            Assert.Throws<ArgumentException>(() => graph.DefineScopeTag("schema"));
            Assert.Throws<ArgumentException>(() => graph.DefineScopeTag("root"));
            Assert.Throws<ArgumentException>(() => graph.DefineScopeTag(""));
            Assert.Throws<ArgumentNullException>(() => graph.DefineScopeTag(null!));

            // Another graph may define the same names: they are different tags.
            var other = new EGraph();
            var otherSchema = other.DefineScopeTag("schema");
            Assert.NotSame(schema, otherSchema);
            Assert.NotSame(graph.RootTag, other.RootTag);
        }

        [Fact]
        public void AnotherGraphsTagIsRefusedEverywhere()
        {
            var t = new ScopeTheory();
            var foreign = new EGraph().DefineScopeTag("foreign");

            Assert.Throws<ArgumentException>(() => t.Root.CreateChild(foreign));
            Assert.Throws<ArgumentException>(() => t.Root.CreateChild(t.Graph.RootTag));
            Assert.Throws<ArgumentException>(() => t.Fact.In(foreign));
            Assert.Throws<ArgumentException>(() => t.Best.In(foreign));
            Assert.Throws<ArgumentException>(() => Fact.In(foreign, t.Fact.Of("a")));
            Assert.Throws<ArgumentException>(() => t.Graph.DefineRuleset("licensed", new RulesetOptions { Licence = foreign }));
            Assert.Throws<ArgumentException>(() => t.Rules.DefineRule("erasing", r =>
            {
                r.Erasing(foreign);
                r.When(t.Fact.Of("a"));
            }));

            // The tag of a split's cases, and of a host-driven case.
            Assert.Throws<ArgumentException>(() => t.Graph.DefineSplit("foreign cases", s => { }, foreign));
            var parent = t.Root.CreateChild();
            using (var cases = parent.OpenCases())
            {
                Assert.Throws<ArgumentException>(() => cases.Add(foreign));
                Assert.Throws<ArgumentException>(() => cases.Add(t.Graph.RootTag));
                Assert.Empty(cases.Cases);
            }

            // Nothing was defined by the refusals.
            Assert.DoesNotContain(t.Graph.Rulesets, r => r.Name == "licensed");
            Assert.Empty(t.Rules.Rules);
            Assert.Empty(t.Graph.SplitRules);
        }

        [Fact]
        public void TwoGraphsShareNoTagStateInEitherOrder()
        {
            // A's schema freezes before B defines its theory (whose first scoped reads create B's mirrors)...
            var a = Compile();
            var b = Compile();
            Assert.True(Ask(a));
            Assert.True(Ask(b));

            // ...and B's theory is defined before A's schema freezes, and A's after B's schema froze.
            var graphB = new EGraph();
            var theoryB = new PredicateTheory(graphB);
            var c = Compile();
            var d = Compile(graphB, theoryB);
            Assert.True(Ask(d));
            Assert.True(Ask(c));

            // Every query case's schema compiles and every query passes, in both orders: A's schemas all
            // freeze before B's theory is defined, and E's theory is defined before F's schemas freeze.
            var reference = Reference();
            var theoryA = new PredicateTheory(new EGraph());
            var schemasA = ScopedQueryCases.CompileAll(theoryA);
            var theoryB2 = new PredicateTheory(new EGraph());
            var schemasB = ScopedQueryCases.CompileAll(theoryB2);
            Assert.Equal(reference, ScopedQueryCases.AskAll(theoryA, schemasA));
            Assert.Equal(reference, ScopedQueryCases.AskAll(theoryB2, schemasB));

            var theoryE = new PredicateTheory(new EGraph());
            var theoryF = new PredicateTheory(new EGraph());
            var schemasF = ScopedQueryCases.CompileAll(theoryF);
            var schemasE = ScopedQueryCases.CompileAll(theoryE);
            Assert.Equal(reference, ScopedQueryCases.AskAll(theoryE, schemasE));
            Assert.Equal(reference, ScopedQueryCases.AskAll(theoryF, schemasF));
        }

        [Fact]
        public void TwoGraphsShareNoTagStateOnParallelThreads()
        {
            var results = new bool[8];
            var threads = Support.DedicatedThreads.For(results.Length, results.Length, i => results[i] = CompileAndAsk());
            Assert.Equal(results.Length, threads);
            Assert.All(results, Assert.True);

            // Every query case's schema and query, one graph per thread.
            var reference = Reference();
            var all = new List<string>[8];
            threads = Support.DedicatedThreads.For(all.Length, all.Length, i =>
            {
                var c = new PredicateTheory(new EGraph());
                all[i] = ScopedQueryCases.AskAll(c, ScopedQueryCases.CompileAll(c));
            });
            Assert.Equal(all.Length, threads);
            Assert.All(all, lines => Assert.Equal(reference, lines));
        }

        [Fact]
        public void AFrozenSchemaOfOneGraphDoesNotRefuseAnotherGraphsFirstScopedRead()
        {
            var a = new EGraph();
            var aTheory = new PredicateTheory(a);
            a.Root.Freeze();
            a.Root.CreateChild(aTheory.Schema).Freeze();
            Assert.Equal(1, a.Mirrors.FrozenCount(aTheory.Schema));

            var b = new EGraph();
            var bTheory = new PredicateTheory(b);
            Assert.Equal(0, b.Mirrors.FrozenCount(bTheory.Schema));
            // The relation present is subsumable, as every relation is, so its mirror has a copy rule for its subsumed rows.
            Assert.Equal(3, b.Mirrors.CopyRules(bTheory.Schema).Length);
            Assert.Equal(new[] { "dom@schema", "present@schema", "subsumed present@schema" },
                b.Mirrors.AllCopyRules().Select(r => r.Rule.Name.Replace("copy ", "", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal));
        }

        /// <summary>
        /// A schema whose order branch declares a stored form for <c>ref</c>, in a new graph.
        /// </summary>
        static (PredicateTheory C, Scope Schema) Compile()
        {
            var graph = new EGraph();
            return Compile(graph, new PredicateTheory(graph));
        }

        /// <summary>
        /// A schema whose order branch declares a stored form for <c>ref</c>, in a graph that has the theory: its root
        /// is frozen, then the schema.
        /// </summary>
        static (PredicateTheory C, Scope Schema) Compile(EGraph graph, PredicateTheory c)
        {
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            schema.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of("order", "invoice")));
            c.DeclareBranch(schema, "kind", "order", c.Form.Of("ref", "uuid-lower"));
            schema.Run(Schedule.Saturate(c.Horn, c.Lattice));
            schema.Freeze();
            return (c, schema);
        }

        /// <summary>
        /// Asks a query that refuses the invoice kind, which leaves only orders, of a compiled schema; returns whether
        /// its UUID comparison lowered.
        /// </summary>
        static bool Ask((PredicateTheory C, Scope Schema) compiled)
        {
            var (c, schema) = compiled;
            using var query = schema.CreateChild(c.Query);
            var q = query.Make(c.And.Of(c.Not.Of(c.StrEq.Of("kind", "invoice")), c.UuidEq.Of("ref", Ref)));
            query.Assume(c.WhereClause.Of(q));
            query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            return query.Extract(q).Mentions(c.UuidEq) == false
                && query.AreEqual(q, c.And.Of(c.Not.Of(c.StrEq.Of("kind", "invoice")), c.StrEq.Of("ref", Ref)));
        }

        /// <summary>
        /// Compiles and asks in a new graph.
        /// </summary>
        static bool CompileAndAsk() => Ask(Compile());

        /// <summary>
        /// Every query case's answers in a graph of its own, which no other graph touches: no case fails, and one line
        /// per discriminated-schema query.
        /// </summary>
        static List<string> Reference()
        {
            var c = new PredicateTheory(new EGraph());
            var lines = ScopedQueryCases.AskAll(c, ScopedQueryCases.CompileAll(c));
            Assert.DoesNotContain(lines, line => line.StartsWith("failed: ", StringComparison.Ordinal));
            return lines;
        }

        /// <summary>
        /// Another graph's tag passed to <c>Fact.In</c> is an <see cref="ArgumentException"/> at that call, inside the
        /// definition of a rule or a query.
        /// </summary>
        [Fact]
        public void FactInWithAnotherGraphsTagIsAnArgumentException()
        {
            var t = new ScopeTheory();
            var foreign = new EGraph().DefineScopeTag("outer");

            Assert.Throws<ArgumentException>(() => t.Rules.DefineRule("foreign tag", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x), Fact.In(foreign, x.Is("a")));
                r.Then(t.Seen.Of(x));
            }));
            Assert.Throws<ArgumentException>(() => t.Graph.DefineQuery("foreign tag", q =>
            {
                var x = q.Var<string>("x");
                q.When(t.Fact.Of(x), Fact.In(foreign, x.Is("a")));
            }));
        }

    }

}
