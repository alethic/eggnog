using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Predicates;
using Eggnog.Tests.Predicates.Scoped;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Whether the order in which sibling schema scopes are compiled changes the plans their runs use: a rule is planned
    /// once, on the statistics of the context that first runs or freezes it, and every context shares the plan. The
    /// corpus is the fixed one <see cref="ScopedQueryCases.CompileAll"/> builds on <see cref="PredicateTheory"/>:
    /// every case's schema, then <see cref="DiscriminatedSchema"/>, as sibling schema scopes under one frozen root
    /// whose statistics differ, each compiled through its own path (declare, run, freeze). It is compiled in that order
    /// and in reverse, in graphs that record plans, and every query is asked of its schema in each.
    /// </summary>
    /// <remarks>
    /// The test checks that every case's result holds in both orders; that each order compiled every schema, the
    /// reverse one the discriminated schema first; that every query reported a global rule's plan; and that within one
    /// order each global rule used one plan in every compile and query, the very plan object its engine rule cached on
    /// its first run rather than a plan of the same text made again. It prints, per order, the global rules that ran and the plans that differ
    /// between the orders: the figure for the risk that a scope compiled later runs on plans made for its sibling.
    /// </remarks>
    public sealed class SiblingScopePlanOrderTests
    {

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="output">The test output.</param>
        public SiblingScopePlanOrderTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void TheFixturesSchemaScopesCompiledInBothOrders()
        {
            var forward = Plans(reverse: false);
            var backward = Plans(reverse: true);

            Assert.NotEmpty(forward);
            Assert.Equal(forward.Keys.Order(), backward.Keys.Order());
            var differ = forward.Keys.Where(rule => forward[rule] != backward[rule]).Order().ToList();
            output.WriteLine($"{forward.Count} global rules ran a recorded plan in both orders; {differ.Count} ran a different plan in reverse order.");
            foreach (var rule in forward.Keys.Order())
                output.WriteLine($"  {rule}{(differ.Contains(rule) ? " (differs)" : "")}: {Flatten(forward[rule])}{(differ.Contains(rule) ? " | reverse: " + Flatten(backward[rule]) : "")}");
        }

        /// <summary>
        /// Compiles the corpus in one order and asks every query; returns the plan each global rule used, and checks
        /// that it used one plan in every run and that every case's result holds.
        /// </summary>
        /// <param name="reverse">Whether to compile the discriminated schema first and the cases in reverse.</param>
        /// <returns>Each global rule's plan, by the rule's name.</returns>
        Dictionary<string, string> Plans(bool reverse)
        {
            var c = new PredicateTheory(new EGraph(new EGraphOptions { RecordPlans = true }));
            c.Graph.Root.Freeze();

            var cases = ScopedQueryCases.All;
            var order = Enumerable.Range(0, cases.Count + 1).ToList();
            if (reverse)
                order.Reverse();

            Assert.Equal(reverse ? cases.Count : 0, order[0]);

            // Each global rule's plan texts, and the plan object each of its engine rules cached when first seen.
            var engine = c.Graph.Root.Engine.Context.Owner;
            var plans = new Dictionary<string, HashSet<string>>();
            var cached = new Dictionary<Relations.EGraphRuleId, object>();
            int Record(RunReport report, string name)
            {
                var recorded = 0;
                foreach (var (rule, statistics) in report.Rules)
                {
                    if (rule.Owner is not null || statistics.Plan is not { } plan)
                        continue;

                    recorded++;
                    (plans.TryGetValue(rule.Name, out var set) ? set : plans[rule.Name] = new HashSet<string>()).Add(plan);
                    foreach (var variant in rule.Compiled.Variants.All)
                    {
                        if (engine.GetRuleInfo(variant.Id).CachedPlan is not { } info)
                            continue;

                        if (cached.TryGetValue(variant.Id, out var first))
                            Assert.True(ReferenceEquals(first, info), $"Global rule '{rule.Name}' was planned again in {name}.");
                        else
                            cached[variant.Id] = info;
                    }
                }

                return recorded;
            }

            // Compile in order; index cases.Count is the discriminated schema, compiled with its split.
            var schemas = new Scope[cases.Count + 1];
            var compiled = 0;
            foreach (var i in order)
            {
                var schema = c.Graph.Root.CreateChild(c.Schema);
                if (i < cases.Count)
                {
                    cases[i].Declare(c, schema);
                    var report = schema.Run(Schedule.Saturate(c.Horn, c.Lattice));
                    Assert.True(report.Saturated, cases[i].Name);
                    Record(report, "the schema of " + cases[i].Name);
                }
                else
                {
                    DiscriminatedSchema.Declare(c, schema);
                    var report = schema.Run(c.Facts);
                    Assert.True(report.Saturated);
                    Assert.Equal(3, report.CasesOpened);
                    Record(report, "the discriminated schema");
                }

                schema.Freeze();
                schemas[i] = schema;
                compiled++;
            }

            Assert.Equal(cases.Count + 1, compiled);
            Assert.All(schemas, schema => Assert.NotNull(schema));

            void Ask(Scope schema, ConstructorTerm<Pred> term, Func<Scope, Pred, bool> holds, string name)
            {
                using var query = schema.CreateChild(c.Query);
                var q = query.Make(term);
                query.Assume(c.WhereClause.Of(q));
                var report = query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
                Assert.True(report.Saturated, name);
                Assert.True(holds(query, q), name);
                Assert.True(Record(report, "the query of " + name) > 0, $"The query of {name} reported no global rule's plan.");
            }

            foreach (var i in order)
            {
                if (i < cases.Count)
                {
                    var queryCase = cases[i];
                    Ask(schemas[i], queryCase.Query(c), (query, q) => queryCase.Holds(c, schemas[i], query, q), queryCase.Name);
                }
                else
                {
                    foreach (var term in DiscriminatedSchema.Queries(c))
                        Ask(schemas[i], term, (query, q) => query.Extract(q) is not null, "discriminated schema: " + term);
                }
            }

            foreach (var (rule, set) in plans)
                Assert.True(set.Count == 1, $"Global rule '{rule}' used {set.Count} plans in one order.");

            return plans.ToDictionary(p => p.Key, p => p.Value.Single());
        }

        /// <summary>
        /// Puts a plan's text on one line.
        /// </summary>
        /// <param name="plan">The plan's text.</param>
        /// <returns>The plan's lines, trimmed and joined.</returns>
        static string Flatten(string plan) => string.Join(" / ", plan.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    }

}
