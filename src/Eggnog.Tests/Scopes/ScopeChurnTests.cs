using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A long-lived graph serving query scopes (<see cref="ScopeChurn"/>) leaves nothing behind per query but what the
    /// graph never reclaims, and that in exact, known amounts: interned values only for new literals, one first id
    /// block per query that makes one term, a fixed number of epochs per query and per case, no child, no local rule,
    /// no external function, no variant, and no engine context. The retained heap is measured in
    /// <see cref="ScopeChurnHeapTests"/>.
    /// </summary>
    public sealed class ScopeChurnTests
    {

        /// <summary>
        /// The derivations of one pass.
        /// </summary>
        const int Queries = 40;

        /// <summary>
        /// The epochs a split query takes for each case it opens, on this theory: <see cref="SplitBaseEpochs"/> plus this
        /// per case, the snapshot read included.
        /// </summary>
        const int EpochsPerCase = 9;

        /// <summary>
        /// The epochs a split query takes beside its cases, the snapshot read included.
        /// </summary>
        const int SplitBaseEpochs = 21;

        /// <summary>
        /// Queries that reuse their literals intern nothing: a second pass of each shape, after a first that interned
        /// whatever was new, leaves <see cref="ValueStore.InternedCount"/> where it was.
        /// </summary>
        [Fact]
        public void QueriesThatReuseTheirLiteralsInternNothing()
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            Pass(churn, _ => churn.Derive(c.StrEq.Of("kind", "a")));
            Pass(churn, _ => churn.Derive(c.NumGt.Of("n", 50L)));
            Pass(churn, _ => churn.Derive(c.Not.Of(c.StrEq.Of("kind", "zz")), churn.Split));
            Pass(churn, _ => churn.Derive(c.StrEq.Of("kind", "a"), localConstant: "kept"));
            var before = churn.Graph.Values.InternedCount;

            Pass(churn, _ => churn.Derive(c.StrEq.Of("kind", "a")));
            Pass(churn, _ => churn.Derive(c.NumGt.Of("n", 50L)));
            Pass(churn, _ => churn.Derive(c.Not.Of(c.StrEq.Of("kind", "zz")), churn.Split));
            Pass(churn, _ => churn.Derive(c.StrEq.Of("kind", "a"), localConstant: "kept"));

            Assert.Equal(before, churn.Graph.Values.InternedCount);
            Assert.Equal(before, churn.Graph.Usage.InternedValues);
        }

        /// <summary>
        /// A query with a new literal interns exactly the values it names or computes, and they stay: a new string
        /// in <c>StrEq</c> interns the string and the singleton set <c>inset</c> computes (2); an <c>Or</c> of a new
        /// and a known equality, the string, its singleton and their union (3); a local rule's new constant, that
        /// constant (1); a small number, nothing (it is stored inline); a split query over a new string, 2 however many
        /// cases it opens.
        /// </summary>
        [Theory]
        [InlineData("StrEq", 2)]
        [InlineData("Or", 3)]
        [InlineData("LocalRule", 1)]
        [InlineData("NumGt", 0)]
        [InlineData("Split", 2)]
        public void AQueryWithANewLiteralInternsExactlyWhatItNames(string shape, int interned)
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            RunReport Derive(int i)
            {
                var fresh = shape + i.ToString(CultureInfo.InvariantCulture);
                return shape switch
                {
                    "StrEq" => churn.Derive(c.StrEq.Of("kind", fresh)),
                    "Or" => churn.Derive(c.Or.Of(c.StrEq.Of("kind", fresh), c.StrEq.Of("kind", "a"))),
                    "LocalRule" => churn.Derive(c.StrEq.Of("kind", "a"), localConstant: fresh),
                    "NumGt" => churn.Derive(c.NumGt.Of("n", 10L + i)),
                    _ => churn.Derive(c.Not.Of(c.StrEq.Of("kind", fresh)), churn.Split),
                };
            }

            // The first queries intern what every query of the shape shares (the known literal's singleton, a union).
            Derive(-1);
            Derive(-2);
            for (var i = 0; i < Queries; i++)
            {
                var before = churn.Graph.Values.InternedCount;
                Derive(i);
                Assert.Equal(interned, churn.Graph.Values.InternedCount - before);
            }
        }

        /// <summary>
        /// The snapshot read that ends a derivation (<c>Check</c>, <c>TryGet</c>, <c>ForEachMatch</c> over a reusable
        /// query) interns nothing and reserves no ids, even for a literal no scope ever wrote.
        /// </summary>
        [Fact]
        public void TheSnapshotReadInternsNothingAndReservesNoIds()
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            using var query = churn.Schema.CreateChild(c.Query);
            var p = query.Make(c.StrEq.Of("kind", "a"));
            query.Assume(c.WhereClause.Of(p));
            query.Run(churn.Plain);
            var before = churn.Graph.Usage;

            churn.ReadSnapshot(query);
            Assert.False(query.Check(c.Ne.Of("kind", "never written anywhere")));
            Assert.False(query.TryGet(c.Dom.Of("never written either"), out _));

            var after = churn.Graph.Usage;
            Assert.Equal(before.InternedValues, after.InternedValues);
            Assert.Equal(before.IdsReserved, after.IdsReserved);
        }

        /// <summary>
        /// Queries that each define a local rule, with a call site of its own, return the engine's rule table, its
        /// external functions and the graph's live variants exactly to where they were, and leave no child under
        /// the schema.
        /// </summary>
        [Fact]
        public void LocalRuleChurnReturnsTheRuleTablesToTheirBaselines()
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            churn.Derive(c.StrEq.Of("kind", "a"), localConstant: "warm");
            var rules = churn.Graph.Engine.RuleCount;
            var functions = ExternalFunctions(churn);
            var variants = churn.Graph.VariantsByDescription.Count;

            // While a query's local rule lives, it holds a rule, a variant and a call site.
            using (var query = churn.Schema.CreateChild(c.Query))
            {
                churn.DefineLocalRule(query, "live", "live");
                Assert.True(churn.Graph.Engine.RuleCount > rules);
                Assert.True(ExternalFunctions(churn) > functions);
                Assert.True(churn.Graph.VariantsByDescription.Count > variants);
            }

            for (var i = 0; i < Queries; i++)
                churn.Derive(c.StrEq.Of("kind", "a"), localConstant: i % 2 == 0 ? "same" : "fresh" + i.ToString(CultureInfo.InvariantCulture));

            Assert.Equal(rules, churn.Graph.Engine.RuleCount);
            Assert.Equal(functions, ExternalFunctions(churn));
            Assert.Equal(variants, churn.Graph.VariantsByDescription.Count);
            Assert.Empty(churn.Schema.Children);
            Assert.Equal(0, churn.Schema.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// The frozen schema has no child left once its queries, plain and split, are disposed.
        /// </summary>
        [Fact]
        public void TheSchemaHasNoChildLeft()
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            Pass(churn, _ => churn.Derive(c.StrEq.Of("kind", "a")));
            Pass(churn, _ => churn.Derive(c.Not.Of(c.StrEq.Of("kind", "zz")), churn.Split));

            Assert.Empty(churn.Schema.Children);
            Assert.Equal(0, churn.Schema.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// A disposed query's engine context is collected after a full collection: nothing the graph keeps refers to
        /// it.
        /// </summary>
        [Fact]
        public void ADisposedQuerysEngineContextIsCollected()
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            var contexts = new List<WeakReference>();
            for (var i = 0; i < 4; i++)
                contexts.Add(churn.DeriveAndTrack(c.StrEq.Of("kind", "a")));

            CollectFully();

            Assert.All(contexts, context => Assert.False(context.IsAlive));
        }

        /// <summary>
        /// A query that makes one term, and whose run makes none, reserves exactly one first id block: the store's id
        /// counter rises by <see cref="Relations.IdBlockAllocator.FirstBlockSize"/> per query, and a split query's
        /// cases, which make no term, add nothing to it.
        /// </summary>
        [Fact]
        public void EachQueryThatMakesOneTermReservesOneFirstBlock()
        {
            var churn = new ScopeChurn(6);
            var c = churn.Theory;
            churn.Derive(c.StrEq.Of("kind", "a"));
            var before = churn.Graph.Usage.IdsReserved;
            Pass(churn, _ => churn.Derive(c.StrEq.Of("kind", "a")));
            Assert.Equal(Queries * (long)Relations.IdBlockAllocator.FirstBlockSize, churn.Graph.Usage.IdsReserved - before);

            churn.Derive(c.IsDefined.Of("kind"), churn.Split);
            before = churn.Graph.Usage.IdsReserved;
            Pass(churn, _ => Assert.Equal(6, churn.Derive(c.IsDefined.Of("kind"), churn.Split).CasesOpened));
            Assert.Equal(Queries * (long)Relations.IdBlockAllocator.FirstBlockSize, churn.Graph.Usage.IdsReserved - before);
        }

        /// <summary>
        /// The clock moves by the same number of epochs for every query of a shape, on a first pass and a second.
        /// </summary>
        [Fact]
        public void TheClockMovesByTheSameCountForEveryQueryOfAShape()
        {
            var churn = new ScopeChurn();
            var c = churn.Theory;
            foreach (var predicate in new[] { c.StrEq.Of("kind", "a"), c.NumGt.Of("n", 50L), c.Or.Of(c.StrEq.Of("kind", "b"), c.StrEq.Of("kind", "a")) })
            {
                churn.Derive(predicate);
                var counts = new HashSet<long>();
                for (var pass = 0; pass < 2; pass++)
                {
                    for (var i = 0; i < Queries / 2; i++)
                    {
                        var before = churn.Graph.Usage.Epochs;
                        churn.Derive(predicate);
                        counts.Add(churn.Graph.Usage.Epochs - before);
                    }
                }

                Assert.Single(counts);
            }
        }

        /// <summary>
        /// A split query takes a fixed number of epochs per case it opens, on top of a fixed base: on this theory,
        /// <see cref="SplitBaseEpochs"/> + <see cref="EpochsPerCase"/> x cases, the snapshot read included, for every
        /// domain size. A change to either constant changes how fast a split workload uses the clock
        /// (<see cref="GraphUsage"/>), so it should be noticed.
        /// </summary>
        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(6)]
        [InlineData(12)]
        public void ASplitQueryTakesAFixedNumberOfEpochsPerCase(int cases)
        {
            var churn = new ScopeChurn(cases);
            var c = churn.Theory;
            churn.Derive(c.Not.Of(c.StrEq.Of("kind", "zz")), churn.Split);
            for (var i = 0; i < 4; i++)
            {
                var before = churn.Graph.Usage.Epochs;
                var report = churn.Derive(c.Not.Of(c.StrEq.Of("kind", "zz")), churn.Split);
                Assert.Equal(cases, report.CasesOpened);
                Assert.Equal(SplitBaseEpochs + EpochsPerCase * cases, churn.Graph.Usage.Epochs - before);
            }
        }

        /// <summary>
        /// One hundred sibling frozen scopes beside the schema, each with one hundred local rules, frozen, queried and
        /// disposed, release everything: every sibling and every compiled variant is collected, the graph's live
        /// variants and the engine's rules are back to their counts before the siblings, and a second pass over the
        /// same constants interns nothing. The retained heap of the two passes is compared in
        /// <see cref="ScopeChurnHeapTests"/>.
        /// </summary>
        [Fact]
        public void HundredSiblingScopesWithLocalRulesReleaseEverything()
        {
            var churn = new ScopeChurn();
            var variants = churn.Graph.VariantsByDescription.Count;
            var rules = churn.Graph.Engine.RuleCount;
            var functions = ExternalFunctions(churn);

            var first = churn.RunSiblings(100, 100);
            CollectFully();
            Assert.DoesNotContain(first, tracked => tracked.IsAlive);
            Assert.Equal(variants, churn.Graph.VariantsByDescription.Count);
            Assert.Equal(rules, churn.Graph.Engine.RuleCount);
            Assert.Equal(functions, ExternalFunctions(churn));
            var interned = churn.Graph.Values.InternedCount;

            var second = churn.RunSiblings(100, 100);
            CollectFully();
            Assert.DoesNotContain(second, tracked => tracked.IsAlive);
            Assert.Equal(variants, churn.Graph.VariantsByDescription.Count);
            Assert.Equal(rules, churn.Graph.Engine.RuleCount);
            Assert.Equal(functions, ExternalFunctions(churn));
            Assert.Equal(interned, churn.Graph.Values.InternedCount);
            Assert.Single(churn.Graph.Root.Children);
        }

        /// <summary>
        /// Runs <see cref="Queries"/> derivations.
        /// </summary>
        static void Pass(ScopeChurn churn, Action<int> derive)
        {
            for (var i = 0; i < Queries; i++)
                derive(i);
        }

        /// <summary>
        /// Gets the number of external functions the engine has registered.
        /// </summary>
        static int ExternalFunctions(ScopeChurn churn) => churn.Graph.Engine.Store.Root.Database.ExternalFunctions.Count;

        /// <summary>
        /// Collects everything unreachable, finalizers included.
        /// </summary>
        static void CollectFully()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

    }

}
