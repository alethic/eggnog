using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// One test per definition error of a rule, with the exact message. A failed definition registers nothing: no rule
    /// and no variant.
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

        /// <summary>
        /// <c>c1.Is(c2)</c> of class constants is never refused. Classes that differ at the root are compared where the
        /// rule runs, so the rule does not match at the root and matches in a child that unions them; classes already
        /// one class at the root are merged, and the rule keeps the first.
        /// </summary>
        [Fact]
        public void ClassConstantsAreComparedByClassWhereTheRuleRuns()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var lit = t.Root.Make(t.Lit.Of(1L));
            t.Root.Assume(t.Tag.Of(a));
            t.Rules.DefineRule("distinct classes", r =>
            {
                r.When(((Term<Node>)a).Is(lit));
                r.Then(t.Num.Of(2L));
            });
            t.Rules.DefineRule("distinct through a variable", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(lit));
                r.Then(t.Num.Of(3L));
            });

            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.Check(t.Num.Of(2L)));
            Assert.False(t.Root.Check(t.Num.Of(3L)));

            var child = t.Root.CreateChild();
            child.Union(a, lit);
            child.Run(Schedule.Saturate(t.Rules));
            Assert.True(child.Check(t.Num.Of(2L)));
            Assert.True(child.Check(t.Num.Of(3L)));
            Assert.False(t.Root.Check(t.Num.Of(3L)));

            t.Root.Union(a, b);
            t.Rules.DefineRule("one class", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                r.Then(t.Num.Of(1L));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(t.Num.Of(1L)));
        }

        [Fact]
        public void AnUnboundVariableIsRefused()
        {
            var t = new NodeTheory();
            AssertRefused(t, "filter", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x));
                r.Where(Int64Primitives.Less.Of(x, y));
            }, "variable 'y' is not bound by any When clause");
            AssertRefused(t, "nested", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x));
                r.Where(Int64Primitives.Less.Of(Int64Primitives.Add.Of(y, 1L), x));
            }, "variable 'y' is not bound by any When clause");
            AssertRefused(t, "head", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x));
                r.Then(t.Num.Of(y));
            }, "variable 'y' is not bound by any When clause");
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

        [Fact]
        public void ClassConstantsFromOtherScopesAreChecked()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            var sibling = t.Root.CreateChild();
            var a = schema.Make(t.A.Of());
            var b = sibling.Make(t.B.Of());

            // A global rule holds only classes from the root.
            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("global", r => r.When(t.Tag.Of(a))));
            Assert.Equal($"class {a.Id} came from scope '{schema.Path}'; a global rule may hold only classes from the root; define the rule on that scope", e.Message);

            // A local rule holds classes from its owner or its ancestors, not from a sibling.
            var local = schema.DefineRule(t.Rules, "local", r => r.When(t.Tag.Of(a)));
            Assert.Same(schema, local.Owner);
            e = Assert.Throws<RuleDefinitionException>(() => schema.DefineRule(t.Rules, "sibling", r => r.When(t.Tag.Of(b))));
            Assert.Equal($"class {b.Id} came from scope '{sibling.Path}', which is not '{schema.Path}' or an ancestor of it", e.Message);

            // A disposed origin.
            sibling.Dispose();
            var child = schema.CreateChild();
            e = Assert.Throws<RuleDefinitionException>(() => child.DefineRule(t.Rules, "disposed", r => r.When(t.Tag.Of(b))));
            Assert.Equal($"class {b.Id} came from scope '{sibling.Path}', which is disposed", e.Message);
        }

        [Fact]
        public void ALocalRulesEquatedConstantsCompareInItsOwner()
        {
            var t = new NodeTheory();
            var hit = t.Graph.DefineRelation<long>("hit");
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            var a = schema.Make(t.A.Of());
            var b = schema.Make(t.B.Of());
            schema.Union(a, b);

            // a and b are one class in the owner, though not in the root.
            schema.DefineRule(t.Rules, "equated", r =>
            {
                r.When(t.Num.Of(1L), ((Term<Node>)a).Is(b));
                r.Then(hit.Of(1L));
            });
            schema.Assume(t.Num.Of(1L));
            schema.Run(Schedule.Saturate(t.Rules));
            Assert.True(schema.Check(hit.Of(1L)));
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
