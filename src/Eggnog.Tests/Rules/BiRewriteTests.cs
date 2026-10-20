using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// egglog's <c>birewrite</c>: <see cref="Ruleset.DefineBiRewrite{TSort}"/> and
    /// <see cref="Scope.DefineBiRewrite{TSort}"/> define the two rewrites egglog desugars it into, <c>{name}=&gt;</c> and
    /// <c>{name}&lt;=</c>, under the same conditions, and register neither when one does not compile.
    /// </summary>
    public sealed class BiRewriteTests
    {

        [Fact]
        public void ABiRewriteIsTwoRewritesNamedAsEgglogNamesThem()
        {
            var t = new NodeTheory();

            var (forward, backward) = t.Rules.DefineBiRewrite<Node>("zero", r =>
            {
                var x = r.Var<Node>("x");
                return (t.Add.Of(x, t.Lit.Of(0L)), t.Wrap.Of(x));
            });

            Assert.Equal("zero=>", forward.Name);
            Assert.Equal("zero<=", backward.Name);
            Assert.Equal(new[] { forward, backward }, t.Rules.Rules);
            Assert.Equal("(rule ((= $rw0 (Add x (Lit 0)))) ((union $rw0 (Wrap x))) :ruleset rules)", forward.ToString());
            Assert.Equal("(rule ((= $rw0 (Wrap x))) ((union $rw0 (Add x (Lit 0)))) :ruleset rules)", backward.ToString());
        }

        [Fact]
        public void ABiRewriteRewritesBothWays()
        {
            var t = new NodeTheory();
            t.Rules.DefineBiRewrite<Node>("zero", r =>
            {
                var x = r.Var<Node>("x");
                return (t.Add.Of(x, t.Lit.Of(0L)), t.Wrap.Of(x));
            });
            var plus = t.Root.Make(t.Add.Of(t.A.Of(), t.Lit.Of(0L)));
            var wrapped = t.Root.Make(t.Wrap.Of(t.B.Of()));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.AreEqual(plus, t.Wrap.Of(t.A.Of())));
            Assert.True(t.Root.AreEqual(wrapped, t.Add.Of(t.B.Of(), t.Lit.Of(0L))));
        }

        [Fact]
        public void TheConditionsHoldInBothDirections()
        {
            var t = new NodeTheory();
            t.Rules.DefineBiRewrite<Node>("tagged", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x));
                return (t.Wrap.Of(x), t.Add.Of(x, x));
            });
            t.Root.Assume(t.Tag.Of(t.A.Of()));
            var tagged = t.Root.Make(t.Add.Of(t.A.Of(), t.A.Of()));
            var untagged = t.Root.Make(t.Wrap.Of(t.B.Of()));
            var untaggedSum = t.Root.Make(t.Add.Of(t.B.Of(), t.B.Of()));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.AreEqual(tagged, t.Wrap.Of(t.A.Of())));
            Assert.False(t.Root.AreEqual(untagged, untaggedSum));
        }

        /// <summary>
        /// A bare variable is not a pattern, so <c>x &lt;= (Wrap x)</c> does not compile, and the direction that does is
        /// not registered either.
        /// </summary>
        [Fact]
        public void NeitherRuleIsRegisteredWhenOneDoesNotCompile()
        {
            var t = new NodeTheory();

            Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineBiRewrite<Node>("unwrap", r =>
            {
                var x = r.Var<Node>("x");
                return (t.Wrap.Of(x), x);
            }));

            Assert.Empty(t.Rules.Rules);
            var wrapped = t.Root.Make(t.Wrap.Of(t.A.Of()));
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.AreEqual(wrapped, t.A.Of()));
        }

        /// <summary>
        /// A refused birewrite registers no mirror. The left-to-right rule would compile, creating the mirror of its
        /// scoped condition, but the right-to-left one does not, so the pair is checked before either compiles; the same
        /// holds for a local birewrite.
        /// </summary>
        [Fact]
        public void ARefusedBiRewriteRegistersNoMirror()
        {
            var t = new ScopeTheory();
            t.Graph.DefineSort<Node>("Node");
            var wrap = t.Graph.DefineConstructor<Node, Node>("Wrap");
            var variants = t.Graph.VariantsByDescription.Count;
            (Term<Node> Left, Term<Node> Right) Unwrap(RuleBuilder r)
            {
                var (x, s) = r.Vars<Node, string>("x", "s");
                r.When(t.Fact.In(t.Outer).Of(s));
                return (wrap.Of(x), x);
            }

            Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineBiRewrite<Node>("unwrap", Unwrap));
            Assert.Throws<RuleDefinitionException>(() => t.Root.DefineBiRewrite<Node>(t.Rules, "unwrap", Unwrap));

            Assert.Empty(t.Graph.Mirrors.AllCopyRules());
            Assert.Equal(variants, t.Graph.VariantsByDescription.Count);
            Assert.Empty(t.Rules.Rules);
            Assert.Empty(t.Root.LocalRules);
        }

        /// <summary>
        /// egglog's <c>birewrite</c> has no head but the rewrite, so a <c>define</c> that adds a head item is refused, and
        /// nothing is registered.
        /// </summary>
        [Fact]
        public void ABiRewritesDefineAddsConditionsOnly()
        {
            var t = new NodeTheory();

            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineBiRewrite<Node>("tagging", r =>
            {
                var x = r.Var<Node>("x");
                r.Then(t.Tag.Of(x));
                return (t.Wrap.Of(x), t.Add.Of(x, x));
            }));

            Assert.Contains("define adds variables and conditions (When, Where) only", e.Message);
            Assert.Empty(t.Rules.Rules);
        }

        [Fact]
        public void ALocalBiRewriteRewritesOnlyInItsScope()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var child = t.Root.CreateChild();
            var sibling = t.Root.CreateChild();

            var (forward, backward) = child.DefineBiRewrite<Node>(t.Rules, "zero", r =>
            {
                var x = r.Var<Node>("x");
                return (t.Add.Of(x, t.Lit.Of(0L)), t.Wrap.Of(x));
            });
            var plus = child.Make(t.Add.Of(t.A.Of(), t.Lit.Of(0L)));
            var other = sibling.Make(t.Add.Of(t.A.Of(), t.Lit.Of(0L)));
            child.Run(Schedule.Saturate(t.Rules));
            sibling.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(new[] { forward, backward }, child.LocalRules);
            Assert.Empty(t.Rules.Rules);
            Assert.True(child.AreEqual(plus, t.Wrap.Of(t.A.Of())));
            Assert.False(sibling.TryGet(t.Wrap.Of(t.A.Of()), out _));
            Assert.True(sibling.AreEqual(other, t.Add.Of(t.A.Of(), t.Lit.Of(0L))));
        }

        [Fact]
        public void ALocalBiRewriteRefusesAnotherGraphsRuleset()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();

            Assert.Throws<ArgumentException>(() => t.Root.DefineBiRewrite<Node>(other.Rules, "zero", r =>
            {
                var x = r.Var<Node>("x");
                return (t.Add.Of(x, t.Lit.Of(0L)), t.Wrap.Of(x));
            }));
        }

    }

}
