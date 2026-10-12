using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// One test per definition error of §6.3 that R1a can reach, with the exact message. A failed definition registers
    /// nothing.
    /// </summary>
    public sealed class RuleDefinitionTests
    {

        [Fact]
        public void AVariableOfAnotherBuilderIsRefused()
        {
            var t = new NodeTheory();
            Var<long>? leaked = null;
            t.Rules.DefineRule("first", r =>
            {
                leaked = r.Var<long>("x");
                r.When(t.Num.Of(leaked));
            });

            AssertRefused(t, "second", r => r.When(t.Num.Of(leaked!)), "variable 'x' belongs to another rule");
        }

        [Fact]
        public void ABuilderCannotBeUsedAfterItsDefinition()
        {
            var t = new NodeTheory();
            RuleBuilder? kept = null;
            t.Rules.DefineRule("first", r => kept = r);
            Assert.Throws<InvalidOperationException>(() => kept!.Var<long>("late"));
        }

        [Fact]
        public void AVariableDeclaredTwiceIsRefused() =>
            AssertRefused(new NodeTheory(), "twice", r =>
            {
                r.Var<long>("x");
                r.Var<string>("x");
            }, "variable 'x' is declared twice");

        [Fact]
        public void AVariableOfAnUnknownTypeIsRefused()
        {
            var t = new NodeTheory();
            var e = AssertRefused(t, "unknown", r => r.Var<Point>("p"), "type Point is not registered; call ValueStore.Register");
            Assert.IsType<ArgumentException>(e.InnerException);
        }

        [Fact]
        public void VariablesOfDifferentTypesEquatedAreRefused()
        {
            var t = new NodeTheory();
            AssertRefused(t, "mixed", r =>
            {
                var a = r.Var(t.Graph.Values.TypeOf<long>(), "a");
                var b = r.Var(t.Graph.Values.TypeOf<string>(), "b");
                r.When(Fact.Equal(a, b));
            }, "variables 'a' (i64) and 'b' (String) are equated");
        }

        [Fact]
        public void ConstantsThatNeverMatchAreRefused()
        {
            var t = new NodeTheory();
            AssertRefused(t, "constants", r => r.When(((Term<long>)1L).Is(2L)), "never matches: 1 = 2 is false");
            AssertRefused(t, "substituted", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x), x.Is(1L), x.Is(2L));
            }, "never matches: 1 = 2 is false");
            t.Rules.DefineRule("equal constants are dropped", r => r.When(((Term<long>)1L).Is(1L)));
        }

        [Fact]
        public void ClassConstantsAreComparedByClassWhenTheRuleIsDefined()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var lit = t.Root.Make(t.Lit.Of(1L));
            AssertRefused(t, "distinct classes", r => r.When(((Term<Node>)a).Is(lit)), $"never matches: #{a.Id.Cell} = #{lit.Id.Cell} is false");
            AssertRefused(t, "distinct through a variable", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(lit));
            }, $"never matches: #{a.Id.Cell} = #{lit.Id.Cell} is false");

            t.Root.Union(a, b);
            t.Rules.DefineRule("one class", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                r.Then(t.Num.Of(1L));
            });
        }

        [Fact]
        public void AnUnboundVariableIsRefused()
        {
            var t = new NodeTheory();
            AssertRefused(t, "filter", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, y));
            }, "variable 'y' is not bound by any When clause");
            AssertRefused(t, "nested", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(Primitives.Add.Of(y, 1L), x));
            }, "variable 'y' is not bound by any When clause");
            AssertRefused(t, "head", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x));
                r.Then(t.Num.Of(y));
            }, "variable 'y' is not bound by any When clause");
        }

        [Fact]
        public void APrimitiveOverASortIsRefused()
        {
            var t = new NodeTheory();
            var odd = Primitive.Create<Node, bool>("odd", n => true);
            AssertRefused(t, "sorted", r =>
            {
                var n = r.Var<Node>("n");
                r.When(t.Tag.Of(n));
                r.Where(odd.Of(n));
            }, "primitive 'odd' takes or returns a sort; classes are compared only with Is and IsNot");
        }

        [Fact]
        public void AnAnalysisWriterReadingAFactTableIsRefused()
        {
            var t = new NodeTheory();
            var present = t.Graph.DefineRelation<string>("present");
            var inset = t.Graph.DefineRelation<Node>("inset", new TableOptions { Role = TableRole.Analysis });
            AssertRefused(t, "writer", r =>
            {
                var (n, p) = r.Vars<Node, string>("n", "p");
                r.When(t.Wrap.Of(n).Is(n), present.Of(p));
                r.Then(inset.Of(n));
            }, "rule writes analysis table 'inset' and may read only term and analysis tables; it reads 'present'");
            t.Rules.DefineRule("a writer reading terms", r =>
            {
                var n = r.Var<Node>("n");
                r.When(Fact.Exists(t.Wrap.Of(n)));
                r.Then(inset.Of(n));
            });
        }

        [Fact]
        public void AFunctionReadInAHeadIsRefused()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            AssertRefused(t, "reads", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Num.Of(f.Of(x)));
            }, "actions do not read; match 'f' in When");
        }

        [Fact]
        public void AValueEqualityInAHeadIsRefused()
        {
            var t = new NodeTheory();
            AssertRefused(t, "values", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x), t.Num.Of(y));
                r.Then(x.Is(y));
            }, "equality of two values that is not a function write: x = y");
        }

        [Fact]
        public void AFilterInAHeadIsRefused()
        {
            var t = new NodeTheory();
            AssertRefused(t, "is not", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x), t.Num.Of(y));
                r.Then(x.IsNot(y));
            }, "head facts are writes; 'IsNot' is a filter");
            AssertRefused(t, "true", r => r.Then(Fact.True(true)), "head facts are writes; 'True' is a filter");
            AssertRefused(t, "exists", r => r.Then(Fact.Exists(t.A.Of())), "head facts are writes; 'Exists' is a filter");
        }

        [Fact]
        public void AMakeVariableInTheBodyIsRefused()
        {
            var t = new NodeTheory();
            AssertRefused(t, "made", r =>
            {
                var m = r.Make("m", t.A.Of());
                r.When(t.Tag.Of(m));
            }, "head variable 'm' made by Make appears in When");
        }

        [Fact]
        public void AHeadVariableUsedBeforeItsMakeIsRefused()
        {
            var t = new NodeTheory();

            // The typed builder cannot express this order; the draft can.
            AssertRefused(t, "early", r =>
            {
                var m = r.Make("m", t.A.Of());
                r.Draft.Head.Insert(0, new HeadItem(HeadItemKind.Then, t.Tag.Of(m), null, null, null));
            }, "head variable 'm' is used before its Make");
        }

        [Fact]
        public void ClassConstantsAreChecked()
        {
            var t = new NodeTheory();
            AssertRefused(t, "default", r => r.When(t.Tag.Of(default(Node))), "a class constant is default(ClassId), which names no class");
            var other = new NodeTheory();
            var foreign = other.Root.Make(other.A.Of());
            AssertRefused(t, "foreign", r => r.Then(t.Tag.Of(foreign)), $"class {foreign.Id} belongs to another graph");
            var mine = t.Root.Make(t.A.Of());
            t.Rules.DefineRule("a root class", r => r.Then(t.Tag.Of(mine)));
        }

        [Fact]
        public void ATableOfAnotherGraphIsRefused()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            AssertRefused(t, "foreign table", r =>
            {
                var x = r.Var<long>("x");
                r.When(other.Num.Of(x));
            }, "table 'num' belongs to another graph");
        }

        [Fact]
        public void AValidRuleIsRegisteredInOrder()
        {
            var t = new NodeTheory();
            var first = t.Rules.DefineRule("first", r => r.Then(t.Num.Of(1L)));
            var second = t.Rules.DefineRule("second", r => r.Then(t.Num.Of(2L)));
            Assert.Equal(new[] { first, second }, t.Rules.Rules);
            Assert.Same(t.Rules, first.Ruleset);
            Assert.Null(first.Owner);
            Assert.False(first.IsNaive);
            Assert.Equal("first", first.Name);
        }

        /// <summary>
        /// Asserts that a definition fails with <paramref name="message"/> and registers nothing.
        /// </summary>
        static RuleDefinitionException AssertRefused(NodeTheory t, string name, Action<RuleBuilder> define, string message)
        {
            var before = t.Rules.Rules.Count;
            var variants = t.Graph.VariantsByDescription.Count;
            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule(name, define));
            Assert.Equal(name, e.RuleName);
            Assert.Equal(message, e.Message);
            Assert.Equal(before, t.Rules.Rules.Count);
            Assert.Equal(variants, t.Graph.VariantsByDescription.Count);
            return e;
        }

    }

}
