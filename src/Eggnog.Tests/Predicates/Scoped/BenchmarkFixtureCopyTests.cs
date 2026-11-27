using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

using Copy = Eggnog.Benchmarks.Library.Predicates;

namespace Eggnog.Tests.Predicates.Scoped
{

    /// <summary>
    /// The query-latency benchmarks' copy of this fixture (<c>Library/Predicates</c> in <c>Eggnog.Benchmarks</c>, whose
    /// files the tests compile too) is faithful: compiled and asked in a graph of its own, it gives every answer
    /// <see cref="PredicateTheory"/> and its corpus give in another, query by query: each case's result and
    /// extraction, and each discriminated-schema query's extraction without the query's case rules and with them, each
    /// extraction with its cost; each case's check judges every query scope as the fixture's does; the corpus's own
    /// <see cref="ScopedQueryCases.AskAll"/> gives the same lines; and the copy defines the same tables, rules (the
    /// schemas' local rules among them) and schedules, and derives the same facts in every schema and query scope.
    /// </summary>
    /// <remarks>
    /// The copy stays as it is, so that the benchmarks compare like with like across builds, while this fixture may
    /// change with its tests. A change to either that changes a definition, a fact, an answer or a check's verdict
    /// fails this test. A deliberate change to this fixture leaves the copy as it is and changes this test instead; any
    /// other failure is a copy that no longer asks what the tests ask. Definitions and facts are compared besides the
    /// answers because a change can leave every answer as it is and still change what a query does, such as a
    /// constructor's cost that no extraction of the corpus reaches, or a condition of a branch rule that no query of
    /// the corpus tells apart; and every case's check is judged on every query scope because a check can still hold of
    /// its own query and judge another answer otherwise.
    /// </remarks>
    public sealed class BenchmarkFixtureCopyTests
    {

        /// <summary>
        /// The copy gives every answer the fixture gives: each query's result and extraction, each check's verdict on
        /// each query scope, and the lines of the corpus's own <see cref="ScopedQueryCases.AskAll"/>.
        /// </summary>
        [Fact]
        public void TheCopyGivesEveryAnswerOfTheFixture()
        {
            var fixture = Fixture();
            var copy = CopyOfFixture();

            Assert.All(fixture.Answers, line => Assert.DoesNotContain("holds: False", line));
            Assert.Equal(fixture.Answers, copy.Answers);
            Assert.Contains(fixture.Checks, line => line.EndsWith(": True", StringComparison.Ordinal));
            Assert.Contains(fixture.Checks, line => line.EndsWith(": True", StringComparison.Ordinal) == false);
            Assert.Equal(fixture.Checks, copy.Checks);
            Assert.DoesNotContain(fixture.AskAll, line => line.StartsWith("failed: ", StringComparison.Ordinal));
            Assert.Equal(fixture.AskAll, copy.AskAll);
        }

        /// <summary>
        /// The copy defines the fixture's tables, global rules, schema-local rules and schedules.
        /// </summary>
        [Fact]
        public void TheCopyDefinesTheFixturesTablesRulesAndSchedules()
        {
            var fixture = Fixture();
            var copy = CopyOfFixture();

            Assert.NotEmpty(fixture.Definitions);
            Assert.Contains(fixture.Definitions, line => line.StartsWith("schema ", StringComparison.Ordinal));
            Assert.Equal(fixture.Definitions, copy.Definitions);
        }

        /// <summary>
        /// The copy derives the fixture's facts in every schema and in every query scope.
        /// </summary>
        [Fact]
        public void TheCopyDerivesTheFixturesFactsInEverySchemaAndQuery()
        {
            var fixture = Fixture();
            var copy = CopyOfFixture();

            Assert.NotEmpty(fixture.Facts);
            Assert.Equal(fixture.Facts, copy.Facts);
        }

        /// <summary>
        /// Compiles this fixture's corpus in a new graph and asks every query of it, each case's without the case rules
        /// and each discriminated-schema query's without them and with them; returns what the graph and its schemas
        /// define, the facts of every schema and query scope, one answer per query, every case's check judged on every
        /// query scope, and the lines of <see cref="ScopedQueryCases.AskAll"/>.
        /// </summary>
        static (List<string> Definitions, List<string> Facts, List<string> Answers, List<string> Checks, List<string> AskAll) Fixture()
        {
            var c = new PredicateTheory(new EGraph());
            var schemas = ScopedQueryCases.CompileAll(c);
            var facts = SchemaFacts(schemas);
            var answers = new List<string>();
            var checks = new List<string>();
            for (var i = 0; i < ScopedQueryCases.All.Count; i++)
            {
                var queryCase = ScopedQueryCases.All[i];
                var schema = schemas[i];
                var (query, q) = DiscriminatedSchema.Ask(c, schema, queryCase.Query(c));
                using (query)
                {
                    Record(facts, answers, queryCase.Name, queryCase.Holds(c, schema, query, q), query, query.Extract(q));
                    Judge(checks, queryCase.Name, ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schema, query, q))));
                }
            }

            foreach (var term in DiscriminatedSchema.Queries(c))
            {
                var (query, q) = DiscriminatedSchema.Ask(c, schemas[^1], term);
                using (query)
                {
                    Record(facts, answers, term.ToString(), null, query, query.Extract(q));
                    Judge(checks, term.ToString(), ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schemas[^1], query, q))));
                }
            }

            foreach (var term in DiscriminatedSchema.Queries(c))
            {
                var name = term.ToString() + " with the case rules";
                var (query, q) = DiscriminatedSchemaTests.AskWithCases(c, schemas[^1], term);
                using (query)
                {
                    Record(facts, answers, name, null, query, query.Extract(q));
                    Judge(checks, name, ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schemas[^1], query, q))));
                }
            }

            return (Definitions(c.Graph, schemas, c.PlainFacts, c.Facts, c.Saturation), facts, answers, checks, ScopedQueryCases.AskAll(c, schemas));
        }

        /// <summary>
        /// Compiles the copy's corpus in a new graph and asks every query of it, as <see cref="Fixture"/> does, with the
        /// copy's own checks and the copy's <c>AskAll</c>.
        /// </summary>
        static (List<string> Definitions, List<string> Facts, List<string> Answers, List<string> Checks, List<string> AskAll) CopyOfFixture()
        {
            var c = new Copy.PredicateTheory(new EGraph());
            var schemas = Copy.ScopedQueryCases.CompileAll(c);
            var facts = SchemaFacts(schemas);
            var answers = new List<string>();
            var checks = new List<string>();
            for (var i = 0; i < Copy.ScopedQueryCases.All.Count; i++)
            {
                var queryCase = Copy.ScopedQueryCases.All[i];
                var schema = schemas[i];
                var (query, q) = Copy.DiscriminatedSchema.Ask(c, schema, queryCase.Query(c));
                using (query)
                {
                    Record(facts, answers, queryCase.Name, queryCase.Holds(c, schema, query, q), query, query.Extract(q));
                    Judge(checks, queryCase.Name, Copy.ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schema, query, q))));
                }
            }

            foreach (var term in Copy.DiscriminatedSchema.Queries(c))
            {
                var (query, q) = Copy.DiscriminatedSchema.Ask(c, schemas[^1], term);
                using (query)
                {
                    Record(facts, answers, term.ToString(), null, query, query.Extract(q));
                    Judge(checks, term.ToString(), Copy.ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schemas[^1], query, q))));
                }
            }

            foreach (var term in Copy.DiscriminatedSchema.Queries(c))
            {
                var name = term.ToString() + " with the case rules";
                var (query, q) = Copy.DiscriminatedSchema.AskWithCases(c, schemas[^1], term);
                using (query)
                {
                    Record(facts, answers, name, null, query, query.Extract(q));
                    Judge(checks, name, Copy.ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schemas[^1], query, q))));
                }
            }

            return (Definitions(c.Graph, schemas, c.PlainFacts, c.Facts, c.Saturation), facts, answers, checks, Copy.ScopedQueryCases.AskAll(c, schemas));
        }

        /// <summary>
        /// Renders what a graph and its schemas define: every table with its kind, columns, subsumption, cost and
        /// lattice; every ruleset's global rules, as written; every schema's local rules, as written, each schema named
        /// by its position; and the schedules given.
        /// </summary>
        static List<string> Definitions(EGraph graph, Scope[] schemas, params ReadOnlySpan<Schedule> schedules)
        {
            var lines = new List<string>();
            foreach (var table in graph.Tables)
            {
                var cost = table is ConstructorHandle constructor ? $", cost {constructor.Cost}" : "";
                var lattice = table is FunctionHandle { Lattice: { } merge } ? $", lattice {merge}" : "";
                lines.Add($"table {table.Name}: {table.Kind} ({string.Join(", ", table.Keys)}) -> {table.Output}, subsumable {table.Subsumable}{cost}{lattice}");
            }

            foreach (var ruleset in graph.Rulesets)
                foreach (var rule in ruleset.Rules)
                    lines.Add($"ruleset {ruleset.Name}: {rule}");

            for (var i = 0; i < schemas.Length; i++)
                foreach (var rule in schemas[i].LocalRules)
                    lines.Add($"schema {i}: {rule}");

            foreach (var schedule in schedules)
                lines.Add($"schedule: {schedule}");

            return lines;
        }

        /// <summary>
        /// Renders the facts of every schema, each named by its position.
        /// </summary>
        static List<string> SchemaFacts(Scope[] schemas)
        {
            var lines = new List<string>();
            for (var i = 0; i < schemas.Length; i++)
                lines.AddRange(ScopeFacts.Of(schemas[i]).Select(fact => $"schema {i}: {fact}"));

            return lines;
        }

        /// <summary>
        /// Records one query's answer, whether the case's result holds (<c>null</c> for a discriminated-schema query)
        /// and the extraction of its predicate, and its saturated query scope's facts.
        /// </summary>
        static void Record(List<string> facts, List<string> answers, string name, bool? holds, Scope query, ExtractedTerm best)
        {
            facts.AddRange(ScopeFacts.Of(query).Select(fact => $"{name}: {fact}"));
            answers.Add($"{name}: {(holds is { } h ? $"holds: {h}, " : "")}{best} at cost {best.Cost}");
        }

        /// <summary>
        /// Records every case's check, in the order of the cases, judged on one query scope: whether it holds there, or
        /// the type of the exception it throws.
        /// </summary>
        static void Judge(List<string> checks, string name, IEnumerable<Func<bool>> checksOfCases)
        {
            var i = 0;
            foreach (var check in checksOfCases)
            {
                string verdict;
                try
                {
                    verdict = check().ToString();
                }
                catch (Exception e)
                {
                    verdict = "throws " + e.GetType().Name;
                }

                checks.Add($"check {i++} on {name}: {verdict}");
            }
        }

    }

}
