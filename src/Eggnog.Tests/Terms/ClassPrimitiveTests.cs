using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Terms
{

    /// <summary>
    /// Primitives over classes, as egglog's take any sort: a class argument is its canonical id where the primitive runs,
    /// in a rule, a host write or read and a merge; <c>ordering-min</c>, <c>ordering-max</c> and <c>bool-!=</c> compare
    /// the ids, as egglog compares representations; a user primitive sees classes of the scope that runs it, and may
    /// return only a class that scope can use, which a host write or read takes canonically. A batch computes over
    /// classes only before it merges classes, and a split's assumptions compute over values only.
    /// </summary>
    public sealed class ClassPrimitiveTests
    {

        [Fact]
        public void AUserPrimitiveFiltersClassesInARule()
        {
            var t = new NodeTheory();
            var calls = 0;
            var any = Primitive.Create<Node, bool>("any-node", n =>
            {
                calls++;
                return n.Id.IsDefault == false;
            });
            var seen = t.Graph.DefineRelation<Node>("seen");
            t.Rules.DefineRule("filter", r =>
            {
                var n = r.Var<Node>("n");
                r.When(t.Tag.Of(n));
                r.Where(any.Of(n));
                r.Then(seen.Of(n));
            });
            t.Root.Assume(t.Tag.Of(t.A.Of()), t.Tag.Of(t.B.Of()));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(2, t.Root.CountRows(seen));
            Assert.Equal(2, calls);
        }

        /// <summary>
        /// <c>ordering-min</c> over classes picks the class with the smaller id, in a rule's head and in a host write; and
        /// a host read computes it over canonical classes.
        /// </summary>
        [Fact]
        public void OrderingMinPicksTheSmallerClass()
        {
            var t = new NodeTheory();
            var pick = t.Graph.DefineFunction<string, Node>("pick");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Rules.DefineRule("min", r =>
            {
                var (x, y) = r.Vars<Node, Node>("x", "y");
                r.When(t.Tag.Of(x), t.Tag.Of(y));
                r.Where(Primitives.NotEqual<Node>().Of(x, y));
                r.Then(pick.Of("rule").Is(Primitives.OrderingMin<Node>().Of(x, y)));
            });
            t.Root.Assume(t.Tag.Of(a), t.Tag.Of(b), pick.Of("host").Is(Primitives.OrderingMin<Node>().Of(b, a)));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(t.Root.AreEqual(t.Root.Get(pick.Of("rule")), t.A.Of()));
            Assert.True(t.Root.AreEqual(t.Root.Get(pick.Of("host")), t.A.Of()));
            Assert.True(t.Root.Check(pick.Of("host").Is(Primitives.OrderingMax<Node>().Of(a, a))));
        }

        /// <summary>
        /// <c>bool-!=</c> over classes compares canonical ids: two classes a union made one are not different.
        /// </summary>
        [Fact]
        public void NotEqualComparesCanonicalClasses()
        {
            var t = new NodeTheory();
            var differ = t.Graph.DefineRelation<Node, Node>("differ");
            t.Rules.DefineRule("differ", r =>
            {
                var (x, y) = r.Vars<Node, Node>("x", "y");
                r.When(t.Tag.Of(x), t.Tag.Of(y));
                r.Where(Primitives.NotEqual<Node>().Of(x, y));
                r.Then(differ.Of(x, y));
            });
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(t.Tag.Of(a), t.Tag.Of(b));
            t.Root.Union(a, b);

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(0, t.Root.CountRows(differ));
            Assert.False(t.Root.Get(Primitives.NotEqual<Node>().Of(a, b)));
        }

        /// <summary>
        /// egglog's <c>:merge (ordering-min old new)</c> over an e-class function keeps the class with the smaller id.
        /// </summary>
        [Fact]
        public void AMergeOfOrderingMinKeepsTheSmallerClass()
        {
            var t = new NodeTheory();
            var least = t.Graph.DefineFunction<string, Node>("least", FunctionMerge.Expression<Node>((old, @new) => Primitives.OrderingMin<Node>().Of(old, @new)));
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(least.Of("k").Is(b));

            t.Root.Assume(least.Of("k").Is(a));
            t.Root.Assume(least.Of("k").Is(b));

            Assert.True(t.Root.AreEqual(t.Root.Get(least.Of("k")), t.A.Of()));
        }

        /// <summary>
        /// A class a primitive returns in a rule's head is canonical, though the host kept it from before a union: a later
        /// rule joins it as its class before any rebuild.
        /// </summary>
        [Fact]
        public void APrimitivesClassResultIsCanonical()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Union(a, b);
            var stale = t.Root.Find(a).Equals(a) ? b : a;
            var keep = Primitive.Create<long, Node>("keep", _ => stale);
            var pick = t.Graph.DefineFunction<string, Node>("pick");
            var joined = t.Graph.DefineRelation<string>("joined");
            t.Rules.DefineRule("keep", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(pick.Of("k").Is(keep.Of(x)));
            });
            var join = t.Graph.DefineRuleset("join");
            join.DefineRule("join", r =>
            {
                var n = r.Var<Node>("n");
                r.When(pick.Of("k").Is(n), t.Tag.Of(n));
                r.Then(joined.Of("k"));
            });
            t.Root.Assume(t.Num.Of(1L), t.Tag.Of(a));

            t.Root.Run(Schedule.Sequence(t.Rules, join));

            Assert.True(t.Root.Check(joined.Of("k")));
        }

        /// <summary>
        /// A primitive may return only a class the scope it runs in can use: one of a child, returned at the root, fails
        /// the rule.
        /// </summary>
        [Fact]
        public void APrimitiveCannotReturnAClassTheScopeCannotUse()
        {
            var t = new NodeTheory();
            var child = t.Root.CreateChild();
            var local = child.Make(t.Lit.Of(7L));
            var leak = Primitive.Create<long, Node>("leak", _ => local);
            var pick = t.Graph.DefineFunction<string, Node>("pick");
            t.Rules.DefineRule("leak", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(pick.Of("k").Is(leak.Of(x)));
            });
            t.Root.Assume(t.Num.Of(1L));

            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));

            Assert.Contains("cannot use", e.ToString(), StringComparison.Ordinal);
            GC.KeepAlive(child);
        }


        /// <summary>
        /// After a union, a class is its canonical id: <c>ordering-min</c> of the class of <c>c</c>, which joined the
        /// smaller <c>a</c>, and <c>b</c> is <c>a</c>'s class, where the raw ids would give <c>b</c>.
        /// </summary>
        [Fact]
        public void OrderingMinSeesCanonicalIdsAfterAUnion()
        {
            var t = new NodeTheory();
            var pick = t.Graph.DefineFunction<string, Node>("pick");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var c = t.Root.Make(t.Lit.Of(3L));
            t.Root.Union(a, c);

            t.Root.Assume(pick.Of("k").Is(Primitives.OrderingMin<Node>().Of(c, b)));

            Assert.True(t.Root.AreEqual(t.Root.Get(pick.Of("k")), t.A.Of()));
            Assert.True(t.Root.AreEqual(t.Root.Get(Primitives.OrderingMin<Node>().Of(c, b)), t.A.Of()));
        }

        /// <summary>
        /// A host read computes over classes usable in the scope it reads only: a child's class is refused at the root.
        /// </summary>
        [Fact]
        public void AHostReadRefusesAClassTheScopeCannotUse()
        {
            var t = new NodeTheory();
            var child = t.Root.CreateChild();
            var local = child.Make(t.Lit.Of(7L));

            Assert.ThrowsAny<ArgumentException>(() => t.Root.Get(Primitives.OrderingMin<Node>().Of(local, local)));
            Assert.True(child.AreEqual(child.Get(Primitives.OrderingMin<Node>().Of(local, local)), t.Lit.Of(7L)));
        }

        /// <summary>
        /// A computation over classes after the batch merged classes sees the merge, as egglog's next command does after
        /// the union's rebuild.
        /// </summary>
        [Fact]
        public void ABatchComputesOverClassesAfterItMergedClasses()
        {
            var t = new NodeTheory();
            var flag = t.Graph.DefineFunction<string, bool>("flag");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());

            t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(flag.Of("k").Is(Primitives.NotEqual<Node>().Of(a, b)));
            });

            Assert.False(t.Root.Get(flag.Of("k")));
        }

        [Fact]
        public void ASplitsAssumptionsComputeOverValuesOnly()
        {
            var t = new NodeTheory();
            var choice = t.Graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var pick = t.Graph.DefineFunction<string, Node>("pick");

            var e = Assert.Throws<RuleDefinitionException>(() => t.Graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)), pick.Of(k).Is(Primitives.OrderingMin<Node>().Of(t.A.Of(), t.B.Of())));
            }));

            Assert.Contains("compute over values", e.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A class a primitive returns in a host write is read canonically: a captured class that a union has since
        /// joined to another is that other's class, and writing it where that class is held conflicts with nothing.
        /// </summary>
        [Fact]
        public void AStaleClassAPrimitiveReturnsIsReadCanonically()
        {
            var t = new NodeTheory();
            var pick = t.Graph.DefineFunction<string, Node>("pick");
            var a = t.Root.Make(t.A.Of());
            var c = t.Root.Make(t.Lit.Of(3L));
            var captured = Primitive.Create<long, Node>("captured", _ => c);
            t.Root.Union(a, c);
            t.Root.Assume(pick.Of("k").Is(t.A.Of()));
            var version = t.Root.Version;

            t.Root.Assume(pick.Of("k").Is(captured.Of(1L)));

            Assert.Equal(version, t.Root.Version);
            Assert.True(t.Root.AreEqual(t.Root.Get(captured.Of(1L)), t.A.Of()));
        }
    }

}
