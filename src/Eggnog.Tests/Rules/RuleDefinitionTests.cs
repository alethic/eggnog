using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// One test per definition error of a rule, with the exact message. A failed definition registers nothing: no rule,
    /// no variant, and no mirror.
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

        [Fact]
        public void NestedFactInIsRefused()
        {
            var t = new ScopeTheory();
            AssertScopedRefused(t, "nested", r => r.When(Fact.In(t.Outer, Fact.In(t.Inner, t.Fact.Of("a")))), "Fact.In cannot be nested");
        }

        [Fact]
        public void AHandleScopedToAnotherTagInsideFactInIsRefused()
        {
            var t = new ScopeTheory();
            AssertScopedRefused(t, "mismatched", r => r.When(Fact.In(t.Inner, t.Fact.In(t.Outer).Of("a"))), "'fact@outer' is inside Fact.In('inner')");
        }

        [Fact]
        public void AScopedReadOfATableWithASortColumnIsRefused()
        {
            var t = new ScopeTheory();
            t.Graph.DefineSort<Node>("Node");
            var and = t.Graph.DefineConstructor<Node, Node, Node>("And");
            var tag = t.Graph.DefineRelation<Node>("tagged");
            AssertScopedRefused(t, "sort column", r =>
            {
                var (a, b) = r.Vars<Node, Node>("a", "b");
                r.When(Fact.In(t.Outer, Fact.Exists(and.Of(a, b))));
            }, "scoped reads are limited to tables whose columns are all values; 'And' has a sort column");
            AssertScopedRefused(t, "sort relation", r => r.When(Fact.In(t.Outer, tag.Of(r.Var<Node>("x")))),
                "scoped reads are limited to tables whose columns are all values; 'tagged' has a sort column");
            Assert.Throws<ArgumentException>(() => ((RelationHandle)tag).InScope(t.Outer));
        }

        [Fact]
        public void AScopedWriteIsRefused()
        {
            var t = new ScopeTheory();
            AssertScopedRefused(t, "scoped insert", r => r.Then(t.Fact.In(t.Outer).Of("a")), "'fact@outer' is read-only");
            AssertScopedRefused(t, "scoped write", r => r.Then(t.Best.In(t.Outer).Of("k").Is(1L)), "'best@outer' is read-only");
            AssertScopedRefused(t, "scoped head", r => r.Then(Fact.In(t.Outer, t.Fact.Of("a"))), "head facts are writes; Fact.In('outer') is read-only");
        }

        [Fact]
        public void AnErasingViolationIsRefused()
        {
            var t = new ScopeTheory();
            var licensed = t.Graph.DefineRuleset("licensed", new RulesetOptions { Licence = t.Outer });
            var e = Assert.Throws<RuleDefinitionException>(() => licensed.DefineRule("own facts", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            }));
            Assert.Equal("rule reads fact table 'fact' outside scope 'outer'; read it through In(outer), or call ReadsOwnFacts with the reason that is sound", e.Message);
            e = Assert.Throws<RuleDefinitionException>(() => licensed.DefineRule("other scope", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Inner).Of(x));
                r.Then(t.Seen.Of(x));
            }));
            Assert.Equal("rule reads fact table 'fact' outside scope 'outer'; read it through In(outer), or call ReadsOwnFacts with the reason that is sound", e.Message);
            Assert.Empty(licensed.Rules);
        }

        [Fact]
        public void ReadsOwnFactsNeedsALicenceAndAReason()
        {
            var t = new ScopeTheory();
            AssertScopedRefused(t, "no licence", r =>
            {
                r.ReadsOwnFacts("because");
                r.When(t.Fact.Of("a"));
            }, "rule calls ReadsOwnFacts but has no licence");
            var licensed = t.Graph.DefineRuleset("licensed", new RulesetOptions { Licence = t.Outer });
            var e = Assert.Throws<RuleDefinitionException>(() => licensed.DefineRule("no reason", r =>
            {
                r.ReadsOwnFacts(" ");
                r.When(t.Fact.Of("a"));
            }));
            Assert.Equal("ReadsOwnFacts needs a reason", e.Message);
        }

        [Fact]
        public void AFirstScopedReadAfterATaggedScopeFrozeIsRefused()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("early", r => r.When(t.Fact.In(t.Outer).Of(r.Var<string>("x"))));
            t.Root.Freeze();
            var outer = t.Root.CreateChild(t.Outer);
            outer.Freeze();

            // fact@outer exists already, so a second reader of it is fine; best@outer would be new.
            t.Rules.DefineRule("second reader", r => r.When(t.Fact.In(t.Outer).Of(r.Var<string>("x"))));
            AssertScopedRefused(t, "late", r =>
            {
                var (k, v) = r.Vars<string, long>("k", "v");
                r.When(v.Is(t.Best.In(t.Outer).Of(k)));
            }, "'best' is first read in scope 'outer' after a scope tagged 'outer' was frozen; define rules that read it before freezing such scopes");

            // Once the frozen scope is released, the read is allowed again; another tag was never affected.
            outer.Dispose();
            t.Rules.DefineRule("after release", r =>
            {
                var (k, v) = r.Vars<string, long>("k", "v");
                r.When(v.Is(t.Best.In(t.Outer).Of(k)));
            });
            t.Rules.DefineRule("inner", r =>
            {
                var (k, v) = r.Vars<string, long>("k", "v");
                r.When(v.Is(t.Best.In(t.Inner).Of(k)));
            });
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
        /// Asserts that a definition in the theory's ruleset fails with <paramref name="message"/> and adds no rule.
        /// </summary>
        static void AssertScopedRefused(ScopeTheory t, string name, Action<RuleBuilder> define, string message)
        {
            var before = t.Rules.Rules.Count;
            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule(name, define));
            Assert.Equal(name, e.RuleName);
            Assert.Equal(message, e.Message);
            Assert.Equal(before, t.Rules.Rules.Count);
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

        /// <summary>
        /// A rule that writes an analysis table may read no scoped atom.
        /// </summary>
        [Fact]
        public void AnAnalysisWriterReadingAScopedAtomIsRefused()
        {
            var t = new ScopeTheory();
            var source = t.Graph.DefineRelation<string>("source", new TableOptions { Role = TableRole.Analysis });
            var analysis = t.Graph.DefineRelation<string>("analysis", new TableOptions { Role = TableRole.Analysis });
            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("analysis from scoped", r =>
            {
                var x = r.Var<string>("x");
                r.When(source.In(t.Outer).Of(x));
                r.Then(analysis.Of(x));
            }));
            Assert.Equal("analysis from scoped", e.RuleName);
            Assert.Equal("rule writes analysis table 'analysis' and may not read scoped atoms; it reads 'source@outer'", e.Message);
            Assert.Empty(t.Rules.Rules);
        }

        /// <summary>
        /// A definition that fails registers nothing: a rule whose body reads a scoped atom and whose head uses an
        /// unbound variable, and a query with an unbound variable over a scoped atom, leave no mirror and no variant.
        /// </summary>
        [Fact]
        public void AFailedDefinitionWithAScopedReadRegistersNoMirror()
        {
            var t = new ScopeTheory();
            var variants = t.Graph.VariantsByDescription.Count;
            Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("unbound head", r =>
            {
                var (x, y) = r.Vars<string, string>("x", "y");
                r.When(t.Fact.In(t.Outer).Of(x));
                r.Then(t.Seen.Of(y));
            }));
            Assert.Throws<RuleDefinitionException>(() => t.Graph.DefineQuery("unbound query", q =>
            {
                var k = q.Var<string>("k");
                q.When(t.Best.In(t.Inner).Of("k").Is(5L));
                q.Where(k, _ => true);
            }));

            Assert.Empty(t.Graph.Mirrors.AllCopyRules());
            Assert.Equal(variants, t.Graph.VariantsByDescription.Count);
        }

        /// <summary>
        /// A failed definition does not count as the first scoped read of a table: after only a failed definition read
        /// <c>fact@outer</c>, freezing a scope tagged outer still refuses the first real reader.
        /// </summary>
        [Fact]
        public void AFailedDefinitionDoesNotCountAsTheFirstScopedRead()
        {
            var t = new ScopeTheory();
            Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("unbound head", r =>
            {
                var (x, y) = r.Vars<string, string>("x", "y");
                r.When(t.Fact.In(t.Outer).Of(x));
                r.Then(t.Seen.Of(y));
            }));
            t.Root.Freeze();
            var outer = t.Root.CreateChild(t.Outer);
            outer.Freeze();

            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("late reader", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Outer).Of(x));
                r.Then(t.Seen.Of(x));
            }));
            Assert.Contains("first read in scope 'outer'", e.Message);
        }

    }

}
