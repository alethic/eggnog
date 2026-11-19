using System;
using System.Collections.Generic;

using Eggnog.Tests.Predicates.Scoped;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Graphs share no state: one graph's theory and frozen schemas never affect another's queries, whichever graph is
    /// defined or frozen first, and on parallel threads.
    /// </summary>
    public sealed class GraphIndependenceTests
    {

        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        [Fact]
        public void TwoGraphsShareNoStateInEitherOrder()
        {
            // A's schema freezes before B defines its theory...
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
        public void TwoGraphsShareNoStateOnParallelThreads()
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
            var schema = graph.Root.CreateChild();
            schema.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(Set.Of(["order", "invoice"])));
            c.DeclareBranch(schema, "kind", "order", forms: [("ref", "uuid-lower")]);
            schema.Run(c.PlainFacts);
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
            using var query = schema.CreateChild();
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

    }

}
