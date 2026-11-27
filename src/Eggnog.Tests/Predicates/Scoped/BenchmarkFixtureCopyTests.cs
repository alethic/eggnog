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
    /// <see cref="ScopedQueryCases.AskAll"/> gives the same lines; compiling the schemas and asking each query take the
    /// same epochs and ids; and the copy defines the same tables, rules (the schemas' local rules among them) and
    /// schedules, as the library renders them, and derives the same facts in every schema and query scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The copy stays as it is, so that the benchmarks compare like with like across builds, while this fixture may
    /// change with its tests. A change to either that changes a definition as rendered, a fact, an answer, a check's
    /// verdict or the epochs and ids of a run fails this test. A deliberate change to this fixture leaves the copy as it
    /// is and changes this test instead; any other failure is a copy that no longer asks what the tests ask.
    /// Definitions, facts and the work of each run are compared besides the answers because a change can leave every
    /// answer as it is and still change what a query does, such as a constructor's cost that no extraction of the
    /// corpus reaches, a condition of a branch rule that no query of the corpus tells apart, or a schedule that reaches
    /// the same fixpoint in other steps; and every case's check is judged on every query scope because a check can
    /// still hold of its own query and judge another answer otherwise.
    /// </para>
    /// <para>
    /// A definition is compared as the library renders it, with each rule's name and each constructor's extractability.
    /// A host lambda renders as <c>&lt;lambda&gt;</c>, so the bodies of the theory's three lambdas, the integer
    /// domain's lower- and upper-bound computations and the guard of the negated string test, are compared only through
    /// the facts, answers and verdicts they give in the corpus and the epochs and ids of its runs. Which schedule a
    /// step runs is not a definition, and nothing renders it: <see cref="DiscriminatedSchema.Ask"/> builds its own
    /// where it runs it, and the other steps each pass one of the theory's schedules to the run. So another schedule
    /// shows only in what its run reaches, the facts and the answers, and in the epochs and ids it takes: one that
    /// reaches the same in as many epochs and ids in every run of the corpus passes.
    /// </para>
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
        /// The copy's runs take the fixture's epochs and ids: compiling every schema, and asking each query without the
        /// case rules and with them. A schedule that reaches the same facts and answers in other steps shows only
        /// there.
        /// </summary>
        [Fact]
        public void TheCopyTakesTheFixturesEpochsAndIdsInEveryRun()
        {
            var fixture = Fixture();
            var copy = CopyOfFixture();

            Assert.Equal(fixture.Answers.Count + 1, fixture.Work.Count);
            Assert.DoesNotContain(fixture.Work, line => line.Contains(": 0 epochs", StringComparison.Ordinal));
            Assert.Equal(fixture.Work, copy.Work);
        }

        /// <summary>
        /// The copy defines the fixture's tables, global rules, schema-local rules and schedules, as the library renders
        /// them, with each rule's name and each constructor's extractability.
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
        /// query scope, the lines of <see cref="ScopedQueryCases.AskAll"/>, and the epochs and ids the compilation and
        /// each ask took.
        /// </summary>
        static (List<string> Definitions, List<string> Facts, List<string> Answers, List<string> Checks, List<string> AskAll, List<string> Work) Fixture()
        {
            var c = new PredicateTheory(new EGraph());
            var compiling = c.Graph.Usage;
            var schemas = ScopedQueryCases.CompileAll(c);
            var work = new List<string> { Work("compiling every schema", compiling, c.Graph.Usage) };
            var facts = SchemaFacts(schemas);
            var answers = new List<string>();
            var checks = new List<string>();
            for (var i = 0; i < ScopedQueryCases.All.Count; i++)
            {
                var queryCase = ScopedQueryCases.All[i];
                var schema = schemas[i];
                var asking = c.Graph.Usage;
                var (query, q) = DiscriminatedSchema.Ask(c, schema, queryCase.Query(c));
                work.Add(Work(queryCase.Name, asking, c.Graph.Usage));
                using (query)
                {
                    Record(facts, answers, queryCase.Name, queryCase.Holds(c, schema, query, q), query, query.Extract(q));
                    Judge(checks, queryCase.Name, ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schema, query, q))));
                }
            }

            foreach (var term in DiscriminatedSchema.Queries(c))
            {
                var asking = c.Graph.Usage;
                var (query, q) = DiscriminatedSchema.Ask(c, schemas[^1], term);
                work.Add(Work(term.ToString(), asking, c.Graph.Usage));
                using (query)
                {
                    Record(facts, answers, term.ToString(), null, query, query.Extract(q));
                    Judge(checks, term.ToString(), ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schemas[^1], query, q))));
                }
            }

            foreach (var term in DiscriminatedSchema.Queries(c))
            {
                var name = term.ToString() + " with the case rules";
                var asking = c.Graph.Usage;
                var (query, q) = DiscriminatedSchemaTests.AskWithCases(c, schemas[^1], term);
                work.Add(Work(name, asking, c.Graph.Usage));
                using (query)
                {
                    Record(facts, answers, name, null, query, query.Extract(q));
                    Judge(checks, name, ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schemas[^1], query, q))));
                }
            }

            return (Definitions(c.Graph, schemas, c.PlainFacts, c.Facts, c.Saturation), facts, answers, checks, ScopedQueryCases.AskAll(c, schemas), work);
        }

        /// <summary>
        /// Compiles the copy's corpus in a new graph and asks every query of it, as <see cref="Fixture"/> does, with the
        /// copy's own checks and the copy's <c>AskAll</c>.
        /// </summary>
        static (List<string> Definitions, List<string> Facts, List<string> Answers, List<string> Checks, List<string> AskAll, List<string> Work) CopyOfFixture()
        {
            var c = new Copy.PredicateTheory(new EGraph());
            var compiling = c.Graph.Usage;
            var schemas = Copy.ScopedQueryCases.CompileAll(c);
            var work = new List<string> { Work("compiling every schema", compiling, c.Graph.Usage) };
            var facts = SchemaFacts(schemas);
            var answers = new List<string>();
            var checks = new List<string>();
            for (var i = 0; i < Copy.ScopedQueryCases.All.Count; i++)
            {
                var queryCase = Copy.ScopedQueryCases.All[i];
                var schema = schemas[i];
                var asking = c.Graph.Usage;
                var (query, q) = Copy.DiscriminatedSchema.Ask(c, schema, queryCase.Query(c));
                work.Add(Work(queryCase.Name, asking, c.Graph.Usage));
                using (query)
                {
                    Record(facts, answers, queryCase.Name, queryCase.Holds(c, schema, query, q), query, query.Extract(q));
                    Judge(checks, queryCase.Name, Copy.ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schema, query, q))));
                }
            }

            foreach (var term in Copy.DiscriminatedSchema.Queries(c))
            {
                var asking = c.Graph.Usage;
                var (query, q) = Copy.DiscriminatedSchema.Ask(c, schemas[^1], term);
                work.Add(Work(term.ToString(), asking, c.Graph.Usage));
                using (query)
                {
                    Record(facts, answers, term.ToString(), null, query, query.Extract(q));
                    Judge(checks, term.ToString(), Copy.ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schemas[^1], query, q))));
                }
            }

            foreach (var term in Copy.DiscriminatedSchema.Queries(c))
            {
                var name = term.ToString() + " with the case rules";
                var asking = c.Graph.Usage;
                var (query, q) = Copy.DiscriminatedSchema.AskWithCases(c, schemas[^1], term);
                work.Add(Work(name, asking, c.Graph.Usage));
                using (query)
                {
                    Record(facts, answers, name, null, query, query.Extract(q));
                    Judge(checks, name, Copy.ScopedQueryCases.All.Select(check => (Func<bool>)(() => check.Holds(c, schemas[^1], query, q))));
                }
            }

            return (Definitions(c.Graph, schemas, c.PlainFacts, c.Facts, c.Saturation), facts, answers, checks, Copy.ScopedQueryCases.AskAll(c, schemas), work);
        }

        /// <summary>
        /// Renders what a graph and its schemas define: every table with its kind, columns, subsumption, cost,
        /// extractability and lattice; every ruleset's global rules, each with its name and as written; every schema's
        /// local rules, each with its name and as written, each schema named by its position; and the schedules given.
        /// </summary>
        static List<string> Definitions(EGraph graph, Scope[] schemas, params ReadOnlySpan<Schedule> schedules)
        {
            var lines = new List<string>();
            foreach (var table in graph.Tables)
            {
                var cost = table is ConstructorHandle constructor ? $", cost {constructor.Cost}, unextractable {constructor.Unextractable}" : "";
                var lattice = table is FunctionHandle { Lattice: { } merge } ? $", lattice {merge}" : "";
                lines.Add($"table {table.Name}: {table.Kind} ({string.Join(", ", table.Keys)}) -> {table.Output}, subsumable {table.Subsumable}{cost}{lattice}");
            }

            foreach (var ruleset in graph.Rulesets)
                foreach (var rule in ruleset.Rules)
                    lines.Add($"ruleset {ruleset.Name}: {rule.Name}: {rule}");

            for (var i = 0; i < schemas.Length; i++)
                foreach (var rule in schemas[i].LocalRules)
                    lines.Add($"schema {i}: {rule.Name}: {rule}");

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

        /// <summary>
        /// Renders the work a run took, from the graph's usage before it to its usage after: the epochs and the ids.
        /// </summary>
        static string Work(string name, GraphUsage before, GraphUsage after) =>
            $"{name}: {after.Epochs - before.Epochs} epochs, {after.IdsReserved - before.IdsReserved} ids";

    }

}
