using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// A host write interns its literals in the order egglog interns a command's: each application or computation is a
    /// <c>let</c> before the action that uses it, a call's literals after those of the calls among its arguments, and the
    /// command's own row or write last (src/core.rs, <c>to_core_actions</c>). The expected cells are the string ids
    /// egglog's <c>--to-json</c> gives the same commands at 5e655cae.
    /// </summary>
    public sealed class LiteralOrderTests
    {

        [Fact]
        public void CallsInternTheirLiteralsBeforeTheirParents()
        {
            // (datatype T (G String) (F String T String) (H T T))
            // (set (k "z") "y") (set (h "a" (k "z")) (+ "b" "c")) (union (H (G "d") (G "e")) (F "f" (G "g") "h"))
            // (set (m (G "i") "j") (+ "k" (k "z") "l")): z0 y1 b2 c3 a4 bc5 d6 e7 g8 f9 h10 i11 k12 l13 j14 kyl15.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var g = graph.DefineConstructor<string, Node>("G");
            var f = graph.DefineConstructor<string, Node, string, Node>("F");
            var h2 = graph.DefineConstructor<Node, Node, Node>("H");
            var k = graph.DefineFunction<string, string>("k");
            var h = graph.DefineFunction<string, string, string>("h");
            var m = graph.DefineFunction<Node, string, string>("m");
            var concat = StringPrimitives.Concat;
            var root = graph.Root;

            root.Assume(k.Of("z").Is("y"));
            root.Assume(h.Of("a", k.Of("z")).Is(concat.Of("b", "c")));
            root.Assume(h2.Of(g.Of("d"), g.Of("e")).Is(f.Of("f", g.Of("g"), "h")));
            root.Assume(m.Of(g.Of("i"), "j").Is(concat.Of("k", k.Of("z"), "l")));

            var strings = graph.Values.TypeOf<string>();
            var expected = new[] { "z", "y", "b", "c", "a", "bc", "d", "e", "g", "f", "h", "i", "k", "l", "j", "kyl" };
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.True(strings.TryEncodeExisting(expected[i], default, out var cell), expected[i]);
                Assert.Equal((uint)i, cell);
            }
        }

        [Fact]
        public void ARowAndAWriteInternTheirOwnLiteralsLast()
        {
            // (datatype T (G String) (F String T String)) (relation r (String T String)) (F "a" (G "b") "c")
            // (r "d" (G "e") "f") (function h (String T) String :no-merge) (set (h "g" (G "i")) "j"):
            // b0 a1 c2 e3 d4 f5 i6 g7 j8.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var g = graph.DefineConstructor<string, Node>("G");
            var f = graph.DefineConstructor<string, Node, string, Node>("F");
            var r = graph.DefineRelation<string, Node, string>("r");
            var h = graph.DefineFunction<string, Node, string>("h");
            var root = graph.Root;

            root.Make(f.Of("a", g.Of("b"), "c"));
            root.Assume(r.Of("d", g.Of("e"), "f"));
            root.Assume(h.Of("g", g.Of("i")).Is("j"));

            var strings = graph.Values.TypeOf<string>();
            var expected = new[] { "b", "a", "c", "e", "d", "f", "i", "g", "j" };
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.True(strings.TryEncodeExisting(expected[i], default, out var cell), expected[i]);
                Assert.Equal((uint)i, cell);
            }
        }

        [Fact]
        public void ARuleInternsItsQueryThenItsActionsInCallOrder()
        {
            // (rule ((= y (F "a" (G "b") "c")) (q "d") (p x "e")) ((p "f" (+ "g" x)) (q "h") (t2 (F "i" (G "j") "k")))),
            // defined before any write: b0 a1 c2 d3 e4 g5 f6 h7 j8 i9 k10.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var g = graph.DefineConstructor<string, Node>("G");
            var f = graph.DefineConstructor<string, Node, string, Node>("F");
            var p = graph.DefineRelation<string, string>("p");
            var q = graph.DefineRelation<string>("q");
            var t2 = graph.DefineRelation<Node>("t2");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("r", r =>
            {
                var (y, x) = r.Vars<Node, string>("y", "x");
                r.When(y.Is(f.Of("a", g.Of("b"), "c")), q.Of("d"), p.Of(x, "e"));
                r.Then(p.Of("f", StringPrimitives.Concat.Of("g", x)), q.Of("h"), t2.Of(f.Of("i", g.Of("j"), "k")));
            });

            var strings = graph.Values.TypeOf<string>();
            var expected = new[] { "b", "a", "c", "d", "e", "g", "f", "h", "j", "i", "k" };
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.True(strings.TryEncodeExisting(expected[i], default, out var cell), expected[i]);
                Assert.Equal((uint)i, cell);
            }
        }

        [Fact]
        public void AQueryInternsAnEqualitysLiteralsAfterItsCalls()
        {
            // (rule ((= (f "a" (G "b")) "c") (= "d" (f "e" (G "g"))) (q x) (!= x "h") (= z (+ "i" x "j"))) ((q z))):
            // b0 a1 c2 g3 e4 d5 h6 i7 j8, a side that is a literal after the calls of both.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var g = graph.DefineConstructor<string, Node>("G");
            var f = graph.DefineFunction<string, Node, string>("f");
            var q = graph.DefineRelation<string>("q");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("r", r =>
            {
                var (x, z) = r.Vars<string, string>("x", "z");
                r.When(
                    f.Of("a", g.Of("b")).Is("c"),
                    ((Term<string>)"d").Is(f.Of("e", g.Of("g"))),
                    q.Of(x),
                    x.IsNot("h"),
                    z.Is(StringPrimitives.Concat.Of("i", x, "j")));
                r.Then(q.Of(z));
            });

            var strings = graph.Values.TypeOf<string>();
            var expected = new[] { "b", "a", "c", "g", "e", "d", "h", "i", "j" };
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.True(strings.TryEncodeExisting(expected[i], default, out var cell), expected[i]);
                Assert.Equal((uint)i, cell);
            }
        }

        [Fact]
        public void AVariableEquatedWithALiteralInternsItWhereTheVariableIsUsed()
        {
            // egglog's canonicalize substitutes the literal for the variable in every atom, and binds the variable at the
            // head's start, the later substitution first: (rule ((R x "b") (= x "a")) ((Q x "c"))) interns a0 b1 c2;
            // (rule ((= x "a") (= y "b") (R "z")) ((Q x y))) z0 b1 a2; (rule ((= "q" "q") (R "z")) ((Q "z" "c"))) drops
            // the equality, z0 c1, and never interns "q".
            AssertInterns(new[] { "a", "b", "c" }, (graph, r, q) =>
                graph.DefineRuleset("rules").DefineRule("r", b =>
                {
                    var x = b.Var<string>("x");
                    b.When(r.Of(x, "b"), x.Is("a"));
                    b.Then(q.Of(x, "c"));
                }));
            AssertInterns(new[] { "z", "b", "a" }, (graph, r, q) =>
                graph.DefineRuleset("rules").DefineRule("r", b =>
                {
                    var (x, y) = b.Vars<string, string>("x", "y");
                    b.When(x.Is("a"), y.Is("b"), r.Of("z", "z"));
                    b.Then(q.Of(x, y));
                }));
            var graph = AssertInterns(new[] { "z", "c" }, (graph, r, q) =>
                graph.DefineRuleset("rules").DefineRule("r", b =>
                {
                    b.When(((Term<string>)"q").Is("q"), r.Of("z", "z"));
                    b.Then(q.Of("z", "c"));
                }));
            Assert.False(graph.Values.TypeOf<string>().TryEncodeExisting("q", default, out _));
        }

        [Fact]
        public void ABiRewriteInternsItsLeftSideThenItsConditions()
        {
            // (birewrite (F "a") (G "b") :when ((has (C "c")))): a0 c1 b2, its left side before its conditions, and the
            // forward rule before the backward one.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var f = graph.DefineConstructor<string, Node>("F");
            var g = graph.DefineConstructor<string, Node>("G");
            var c = graph.DefineConstructor<string, Node>("C");
            var has = graph.DefineRelation<Node>("has");
            graph.DefineRuleset("rules").DefineBiRewrite<Node>("fg", r =>
            {
                r.When(has.Of(c.Of("c")));
                return (f.Of("a"), g.Of("b"));
            });

            AssertCells(graph, "a", "c", "b");
        }

        [Fact]
        public void ARefusedRuleInternsNothing()
        {
            // egglog typechecks a rule before it lowers it, so a rule it refuses interns no literal.
            var graph = new EGraph();
            var r = graph.DefineRelation<string, string>("R");
            var q = graph.DefineRelation<string, string>("Q");
            var rules = graph.DefineRuleset("rules");
            var before = graph.Values.InternedCount;

            Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("twice", b =>
            {
                var x = b.Var<string>("x");
                b.Var<string>("x");
                b.When(r.Of(x, "zz"));
                b.Then(q.Of(x, "yy"));
            }));
            Assert.Throws<RuleDefinitionException>(() => rules.DefineRule("unbound", b =>
            {
                var (x, y) = b.Vars<string, string>("x", "y");
                b.When(r.Of(x, "zz"));
                b.Then(q.Of(y, "yy"));
            }));
            Assert.Throws<RuleDefinitionException>(() => graph.DefineQuery("unbound", b =>
            {
                var (x, y) = b.Vars<string, string>("x", "y");
                b.When(r.Of(x, "zz"));
            }));

            Assert.Equal(before, graph.Values.InternedCount);
        }

        [Fact]
        public void ContainerAndBigIntConstantsInternAsTheCallsTheyRenderAs()
        {
            // (F2 "x" (set-of "a" "b")) (F3 "y" (vec-of "d" "c")) (B (from-string "123") "p" "q"):
            // a0 b1 x2 d3 c4 y5 "123"6 p7 q8, each container's elements before its parent's literals.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            graph.Values.RegisterVec<string>();
            var f2 = graph.DefineConstructor<string, Set<string>, Node>("F2");
            var f3 = graph.DefineConstructor<string, Vec<string>, Node>("F3");
            var b = graph.DefineConstructor<System.Numerics.BigInteger, string, string, Node>("B");
            var root = graph.Root;

            root.Make(f2.Of("x", Set.Of("a", "b")));
            root.Make(f3.Of("y", Vec.Of("d", "c")));
            root.Make(b.Of(new System.Numerics.BigInteger(123), "p", "q"));

            AssertCells(graph, "a", "b", "x", "d", "c", "y", "123", "p", "q");

            // (R (bigrat (from-string "3") (from-string "4")) "x"): "3"0 "4"1 x2.
            var rationals = new EGraph();
            rationals.DefineSort<Node>("T");
            var r = rationals.DefineConstructor<BigRational, string, Node>("R");
            rationals.Root.Make(r.Of(new BigRational(3, 4), "x"));
            AssertCells(rationals, "3", "4", "x");
        }

        /// <summary>
        /// Defines rules over <c>R</c> and <c>Q</c>, relations of two strings, in a new graph, and checks the strings'
        /// cells.
        /// </summary>
        static EGraph AssertInterns(string[] expected, System.Action<EGraph, Relation<string, string>, Relation<string, string>> define)
        {
            var graph = new EGraph();
            var r = graph.DefineRelation<string, string>("R");
            var q = graph.DefineRelation<string, string>("Q");
            define(graph, r, q);
            AssertCells(graph, expected);
            return graph;
        }

        /// <summary>
        /// Checks that the strings have the cells 0, 1, 2… in order.
        /// </summary>
        static void AssertCells(EGraph graph, params string[] expected)
        {
            var strings = graph.Values.TypeOf<string>();
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.True(strings.TryEncodeExisting(expected[i], default, out var cell), expected[i]);
                Assert.Equal((uint)i, cell);
            }
        }

        [Fact]
        public void ADeletionAndASubsumptionInternTheirKeys()
        {
            // (function f (String) i64 :no-merge) (delete (f "zz")) (datatype T (C String)) (set (f "a") 1) (C "b")
            // (subsume (C "qq")) (C "c"): zz0 a1 b2 qq3 c4, the deletion's key interned though it deletes nothing.
            var graph = new EGraph();
            graph.DefineSort<Node>("T");
            var f = graph.DefineFunction<string, long>("f");
            var c = graph.DefineConstructor<string, Node>("C", options: new TableOptions { Subsumable = true });
            var root = graph.Root;

            root.Delete(f.Of("zz"));
            root.Assume(f.Of("a").Is(1L));
            root.Make(c.Of("b"));
            root.Subsume(c.Of("qq"));
            root.Make(c.Of("c"));

            var strings = graph.Values.TypeOf<string>();
            var expected = new[] { "zz", "a", "b", "qq", "c" };
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.True(strings.TryEncodeExisting(expected[i], default, out var cell), expected[i]);
                Assert.Equal((uint)i, cell);
            }
        }

    }

}
