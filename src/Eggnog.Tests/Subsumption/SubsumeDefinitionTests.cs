using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Subsumption
{

    /// <summary>
    /// Which tables may be subsumed and where a subsumption may be written: a relation or a constructor opts in with
    /// <see cref="TableOptions.Subsumable"/>, a function cannot (egglog's <c>test_subsume_custom</c> and
    /// <c>test_cant_subsume_merge</c>), and rules and hosts subsume only tables that opted in, only through the running
    /// scope, and only relation rows and constructor terms. Rules render their subsumptions as egglog writes them.
    /// </summary>
    public sealed class SubsumeDefinitionTests
    {

        /// <summary>
        /// As in egglog, every relation and constructor is subsumable and no function is; a relation or constructor may
        /// opt out, which stores no subsumption cell.
        /// </summary>
        [Fact]
        public void RelationsAndConstructorsAreSubsumableUnlessTheyOptOut()
        {
            var t = new MathTheory();
            var tag = t.Graph.DefineScopeTag("schema");
            var fact = t.Graph.DefineRelation<string>("fact", MathTheory.Subsumable);
            var plain = t.Graph.DefineConstructor<long, Node>("Plain", 1, MathTheory.NotSubsumable);
            var byDefault = t.Graph.DefineConstructor<long, Node>("ByDefault");
            var relation = t.Graph.DefineRelation<string>("relation");
            var function = t.Graph.DefineFunction<string, long>("function");

            Assert.True(t.Num.Subsumable);
            Assert.True(fact.Subsumable);
            Assert.True(fact.In(tag).Subsumable);
            Assert.True(byDefault.Subsumable);
            Assert.True(relation.Subsumable);
            Assert.False(function.Subsumable);
            Assert.False(plain.Subsumable);
            Assert.False(t.Seen.Subsumable);
            Assert.Null(new TableOptions().Subsumable);
        }

        [Fact]
        public void AFunctionCannotBeSubsumable()
        {
            var graph = new EGraph();

            var plain = Assert.Throws<ArgumentException>(() => graph.DefineFunction<long>("one", null, MathTheory.Subsumable));
            Assert.Contains("only relations and constructors can be subsumed", plain.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() =>
                graph.DefineFunction<string, long>("best", ValueLattice.Max<long>(), MathTheory.Subsumable));
            Assert.DoesNotContain(graph.Tables, table => table.Name is "one" or "best");
        }

        [Fact]
        public void ARuleCannotSubsumeATableThatOptedOut()
        {
            var t = new MathTheory();
            var plain = t.Graph.DefineConstructor<long, Node>("Plain", 1, MathTheory.NotSubsumable);

            var term = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("plain term", r =>
            {
                var n = r.Var<long>("n");
                r.When(Fact.Exists(plain.Of(n)));
                r.Subsume(plain.Of(n));
            }));
            Assert.Equal("plain term", term.RuleName);
            Assert.Contains("'Plain' is not subsumable", term.Message, StringComparison.Ordinal);

            Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("plain row", r =>
            {
                var n = r.Var<long>("n");
                r.When(t.Seen.Of(n));
                r.Subsume(t.Seen.Of(n));
            }));

            Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("plain rewrite", r =>
            {
                var n = r.Var<long>("n");
                r.Rewrite(plain.Of(n), t.Num.Of(n), subsume: true);
            }));

            Assert.Empty(t.Rules.Rules);
        }

        [Fact]
        public void ASubsumingRewriteNeedsAConstructorApplicationOnTheLeft()
        {
            var t = new MathTheory();

            // egglog: "Subsumed rewrite must have a function call on the lhs".
            var error = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("variable", r =>
            {
                var (x, y) = r.Vars<Node, Node>("x", "y");
                r.When(Fact.Exists(t.Add.Of(x, y)));
                r.Rewrite(x, t.Add.Of(y, x), subsume: true);
            }));
            Assert.Contains("a subsuming rewrite's left side must be a constructor application", error.Message, StringComparison.Ordinal);

            // The same rewrite without subsume is fine.
            t.Rules.DefineRule("plain", r =>
            {
                var (x, y) = r.Vars<Node, Node>("x", "y");
                r.When(Fact.Exists(t.Add.Of(x, y)));
                r.Rewrite(x, t.Add.Of(y, x), subsume: false);
            });
        }

        [Fact]
        public void ARuleSubsumesRelationRowsAndConstructorTermsOnly()
        {
            var t = new MathTheory();
            var fact = t.Graph.DefineRelation<long>("fact", MathTheory.Subsumable);
            var tag = t.Graph.DefineScopeTag("schema");

            var equality = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("equality", r =>
            {
                var (x, y) = r.Vars<Node, Node>("x", "y");
                r.When(Fact.Exists(t.Add.Of(x, y)));
                r.Subsume(x.Is(y));
            }));
            Assert.Contains("Subsume takes relation rows and constructor terms", equality.Message, StringComparison.Ordinal);

            // A scoped view is read-only, in a subsumption as in any write.
            var scoped = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("scoped", r =>
            {
                var n = r.Var<long>("n");
                r.When(fact.Of(n));
                r.Subsume(fact.In(tag).Of(n));
            }));
            Assert.Contains("'fact@schema' is read-only", scoped.Message, StringComparison.Ordinal);
            var scopedFact = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("scoped fact", r =>
            {
                var n = r.Var<long>("n");
                r.When(fact.Of(n));
                r.Subsume(Fact.In(tag, fact.Of(n)));
            }));
            Assert.Contains("Fact.In('schema') is read-only", scopedFact.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnAnalysisIsSubsumedOnlyByRulesThatReadTermsAndAnalyses()
        {
            var t = new MathTheory();
            var fact = t.Graph.DefineRelation<long>("fact");
            var analysis = t.Graph.DefineRelation<long>("analysis", new TableOptions { Role = TableRole.Analysis, Subsumable = true });

            // Subsuming is writing: a rule that subsumes an analysis may not judge on facts.
            var error = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("judges on a fact", r =>
            {
                var n = r.Var<long>("n");
                r.When(fact.Of(n), analysis.Of(n));
                r.Subsume(analysis.Of(n));
            }));
            Assert.Contains("rule writes analysis table 'analysis'", error.Message, StringComparison.Ordinal);

            t.Rules.DefineRule("reads a term", r =>
            {
                var n = r.Var<long>("n");
                r.When(Fact.Exists(t.Num.Of(n)), analysis.Of(n));
                r.Subsume(analysis.Of(n));
            });
        }

        [Fact]
        public void RulesRenderTheirSubsumptionsAsEgglogWritesThem()
        {
            var t = new MathTheory();
            var fact = t.Graph.DefineRelation<long>("fact", MathTheory.Subsumable);

            // (rewrite (Mul a (Num 2)) (Add a a) :subsume) desugars to a union, then a subsume of the left side.
            var rewrite = t.Rules.DefineRule("double", r =>
            {
                var a = r.Var<Node>("a");
                r.Rewrite(t.Mul.Of(a, t.Num.Of(2L)), t.Add.Of(a, a), subsume: true);
            });
            var row = t.Rules.DefineRule("prune", r =>
            {
                var n = r.Var<long>("n");
                r.When(fact.Of(n));
                r.Subsume(fact.Of(n));
                r.Subsume(t.Num.Of(n));
            });

            Assert.Equal(
                "(rule ((= $rw0 (Mul a (Num 2)))) ((union $rw0 (Add a a)) (subsume (Mul a (Num 2)))) :ruleset rules)",
                rewrite.ToString());
            Assert.Equal("(rule ((fact n)) ((subsume (fact n)) (subsume (Num n))) :ruleset rules)", row.ToString());
        }

        [Fact]
        public void AHostSubsumesRelationRowsAndConstructorTermsButNotOfTablesThatOptedOut()
        {
            var t = new MathTheory();
            var plain = t.Graph.DefineConstructor<long, Node>("Plain", 1, MathTheory.NotSubsumable);
            var fact = t.Graph.DefineRelation<long>("fact", MathTheory.Subsumable);
            var tag = t.Graph.DefineScopeTag("schema");
            var version = t.Root.Version;

            var refused = Assert.Throws<ArgumentException>(() => t.Root.Subsume(plain.Of(1L)));
            Assert.Contains("'Plain' is not subsumable", refused.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => t.Root.Subsume(t.Seen.Of(1L)));
            Assert.Throws<ArgumentException>(() => t.Root.Subsume(fact.In(tag).Of(1L)));
            Assert.Throws<ArgumentException>(() => t.Root.Subsume(Fact.In(tag, fact.Of(1L))));
            Assert.Throws<ArgumentException>(() => t.Root.Subsume(t.Num.Of(1L).Is(t.Num.Of(2L))));

            // A refused row writes nothing, and the rows before it stand, as egglog's commands do.
            Assert.Equal(version, t.Root.Version);
            Assert.Throws<ArgumentException>(() => t.Root.Subsume(fact.Of(1L), t.Seen.Of(1L)));
            Assert.Equal(1L, t.Root.CountRows(fact));
            Assert.Equal(0L, t.Root.CountRows(plain));
        }

    }

}
