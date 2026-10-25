using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Sets of classes, egglog's eq-sort <c>Set</c> containers: elements that become one become one element, and
    /// <c>set-get</c> indexes the elements in cell order, as egglog's rebuild and <c>BTreeSet</c> at 5e655cae make them.
    /// </summary>
    public sealed class ClassSetTests
    {

        sealed class Theory
        {

            public Theory()
            {
                Graph.DefineSort<Node>("Math");
                Num = Graph.DefineConstructor<long, Node>("Num");
                Graph.Values.RegisterSet<Node>();
                S = Graph.DefineConstructor<Set<Node>, Node>("S");
                Sets = Graph.DefineFunction<Set<Node>>("s");
            }

            public EGraph Graph { get; } = new();

            public Scope Root => Graph.Root;

            public Constructor<long, Node> Num { get; }

            public Constructor<Set<Node>, Node> S { get; }

            public Function<Set<Node>> Sets { get; }

            public Term<Set<Node>> Of(params long[] numbers) => Primitives.SetOf<Node>().Of(numbers.Select(n => (Term<Node>)Num.Of(n)).ToArray());

            public Node Make(long n) => Root.Make(Num.Of(n));

        }

        [Fact]
        public void ElementsThatBecomeOneBecomeOneElementAndGetReadsCellOrder()
        {
            // (set (s) (set-of (Num 3) (Num 2) (Num 1))) (let x (S (set-of (Num 2) (Num 1)))), then (union (Num 1) (Num 2)):
            // egglog extracts x as (S (set-of (Num 2) (Num 1))), then (S (set-of (Num 2))); (set-get (s) 0) is (Num 3)
            // throughout, (set-get (s) 2) is (Num 1), then (set-get (s) 1) is (Num 2) and the length 2.
            var t = new Theory();
            t.Root.Assume(t.Sets.Of().Is(t.Of(3L, 2L, 1L)));
            var x = t.Root.Make(t.S.Of(t.Of(2L, 1L)));
            var get = Primitives.Get<Node>();
            Assert.Equal("(S (set-of (Num 2) (Num 1)))", t.Root.Extract(x).ToString());
            Assert.True(t.Root.Check(get.Of(t.Sets.Of(), 0L).Is(t.Num.Of(3L))));
            Assert.True(t.Root.Check(get.Of(t.Sets.Of(), 2L).Is(t.Num.Of(1L))));
            Assert.True(t.Root.Check(Primitives.Contains<Node>().Of(t.Sets.Of(), t.Num.Of(2L)).Is(true)));

            t.Root.Union(t.Make(1L), t.Make(2L));

            Assert.Equal("(S (set-of (Num 2)))", t.Root.Extract(x).ToString());
            Assert.True(t.Root.Check(Primitives.Count<Node>().Of(t.Sets.Of()).Is(2L)));
            Assert.True(t.Root.Check(get.Of(t.Sets.Of(), 0L).Is(t.Num.Of(3L))));
            Assert.True(t.Root.Check(get.Of(t.Sets.Of(), 1L).Is(t.Num.Of(1L))));
            Assert.False(t.Root.Check(get.Of(t.Sets.Of(), 2L).Is(t.Num.Of(1L))));
            Assert.Equal(Set.Of(t.Root.Find(t.Make(3L)), t.Root.Find(t.Make(1L))), t.Root.Get(t.Sets.Of()));
        }

        [Fact]
        public void SetsOfClassesSerializeAsEgglogs()
        {
            // egglog --to-json of the program above: the set x holds is rebuilt in place, its id kept, and its one element
            // takes the class's next node.
            var t = new Theory();
            t.Root.Assume(t.Sets.Of().Is(t.Of(3L, 2L, 1L)));
            t.Root.Make(t.S.Of(t.Of(2L, 1L)));
            t.Root.Union(t.Make(1L), t.Make(2L));

            var serialized = t.Root.Serialize();
            var nodes = serialized.Nodes.Select(n => $"{n.Id} {n.Op} [{string.Join(", ", n.Children)}] {n.Class}");
            Assert.Equal(
                new[]
                {
                    "primitive-i64-3 3 [] i64-3",
                    "function-0-Num Num [primitive-i64-3] Math-0",
                    "primitive-i64-2 2 [] i64-2",
                    "function-1-Num Num [primitive-i64-2] Math-1",
                    "primitive-i64-1 1 [] i64-1",
                    "function-2-Num Num [primitive-i64-1] Math-1",
                    "primitive-Set<Math>-4 set-of [function-2-Num] Set<Math>-4",
                    "function-0-S S [primitive-Set<Math>-4] Math-5",
                    "primitive-Set<Math>-3 set-of [function-0-Num, function-1-Num] Set<Math>-3",
                    "function-0-s s [] Set<Math>-3",
                },
                nodes);
        }

        [Fact]
        public void AFunctionKeyedBySetsWhoseElementsBecomeOneConflicts()
        {
            // (function g (MS) i64 :no-merge): {(Num 1), (Num 2)} and {(Num 1)} become one set when (Num 1) and (Num 2) are
            // one class, so the rows meet with two values, which egglog's rebuild panics on.
            var t = new Theory();
            var g = t.Graph.DefineFunction<Set<Node>, long>("g");
            t.Root.Assume(g.Of(t.Of(1L, 2L)).Is(10L), g.Of(t.Of(1L)).Is(20L));

            Assert.Throws<FactConflictException>(() => t.Root.Union(t.Make(1L), t.Make(2L)));
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public void ElementsThatBecomeOneInAChildBecomeOneElementThereOnly()
        {
            // Probe set-child.egg, which egglog at 5e655cae runs clean: the program of
            // ElementsThatBecomeOneBecomeOneElementAndGetReadsCellOrder, with g of (set-of (Num 3) (Num 4)) 10 and of
            // (set-of (Num 3)) 20, and (union (Num 1) (Num 2)) in a push, where (extract $x) is (S (set-of (Num 2))),
            // (s) has two elements, (set-get (s) 1) is (Num 1) and none is at 2, and (fail (union (Num 3) (Num 4)))
            // panics as g's rows would meet; in a second push the same union by a rule; after the pops (extract $x) is
            // (S (set-of (Num 2) (Num 1))) and (s) has three. The first union is the child's host merge, which the batch
            // checks against the sets of the child's view.
            var t = new Theory();
            var g = t.Graph.DefineFunction<Set<Node>, long>("g");
            var go = t.Graph.DefineRelation("go");
            var merge = t.Graph.DefineRuleset("merge");
            merge.DefineRule("merge", r =>
            {
                r.When(go.Of());
                r.Then(t.Num.Of(1L).Is(t.Num.Of(2L)));
            });
            t.Root.Assume(t.Sets.Of().Is(t.Of(3L, 2L, 1L)));
            var x = t.Root.Make(t.S.Of(t.Of(2L, 1L)));
            t.Root.Assume(g.Of(t.Of(3L, 4L)).Is(10L), g.Of(t.Of(3L)).Is(20L));
            var get = Primitives.Get<Node>();
            t.Root.Freeze();

            using (var child = t.Root.CreateChild())
            {
                child.Union(child.Make(t.Num.Of(1L)), child.Make(t.Num.Of(2L)));
                Assert.Equal("(S (set-of (Num 2)))", child.Extract(x).ToString());
                Assert.True(child.Check(Primitives.Count<Node>().Of(t.Sets.Of()).Is(2L)));
                Assert.True(child.Check(get.Of(t.Sets.Of(), 0L).Is(t.Num.Of(3L))));
                Assert.True(child.Check(get.Of(t.Sets.Of(), 1L).Is(t.Num.Of(1L))));
                Assert.False(child.Check(get.Of(t.Sets.Of(), 2L).Is(t.Num.Of(1L))));
                Assert.True(child.Check(t.Sets.Of().Is(t.Of(3L, 1L))));
                Assert.True(child.Check(t.S.Of(t.Of(2L)).Is(x)));
                Assert.Throws<FactConflictException>(() => child.Union(child.Make(t.Num.Of(3L)), child.Make(t.Num.Of(4L))));
                Assert.False(child.IsFaulted);
                Assert.Equal(2, child.CountRows(g));
                child.Engine.Context.CheckInvariants();
            }

            using (var child = t.Root.CreateChild())
            {
                child.Assume(go.Of());
                child.Run(merge);
                Assert.True(child.Check(Primitives.Count<Node>().Of(t.Sets.Of()).Is(2L)));
                Assert.True(child.Check(t.S.Of(t.Of(1L)).Is(x)));
            }

            Assert.Equal("(S (set-of (Num 2) (Num 1)))", t.Root.Extract(x).ToString());
            Assert.True(t.Root.Check(Primitives.Count<Node>().Of(t.Sets.Of()).Is(3L)));
            Assert.True(t.Root.Check(get.Of(t.Sets.Of(), 2L).Is(t.Num.Of(1L))));
        }

        [Fact]
        public void AConstantSetOfStaleClassesHoldsTheirClass()
        {
            var t = new Theory();
            var (one, two) = (t.Make(1L), t.Make(2L));
            t.Root.Union(one, two);

            t.Root.Assume(t.Sets.Of().Is(Set.Of(one, two)));
            Assert.Equal(Set.Of(t.Root.Find(one)), t.Root.Get(t.Sets.Of()));
            Assert.True(t.Root.Check(t.Sets.Of().Is(Set.Of(two))));
        }

        [Fact]
        public void ARuleMatchesTheElementsOfASetOfClasses()
        {
            // (rule ((= xs (s)) (set-contains xs (Num 2))) ((hit (set-get xs 0)))).
            var t = new Theory();
            var hit = t.Graph.DefineRelation<Node>("hit");
            var rules = t.Graph.DefineRuleset("r");
            rules.DefineRule("contains", r =>
            {
                var xs = r.Var<Set<Node>>("xs");
                r.When(xs.Is(t.Sets.Of()), Fact.True(Primitives.Contains<Node>().Of(xs, t.Num.Of(2L))));
                r.Then(hit.Of(Primitives.Get<Node>().Of(xs, 0L)));
            });
            t.Root.Assume(t.Sets.Of().Is(t.Of(3L, 2L)));
            t.Root.Run(rules);

            Assert.True(t.Root.Check(hit.Of(t.Num.Of(3L))));
            Assert.False(t.Root.Check(hit.Of(t.Num.Of(2L))));
        }

        [Fact]
        public void AFailingRuleRendersASetOfClassesByItsCells()
        {
            var t = new Theory();
            var rules = t.Graph.DefineRuleset("r");
            rules.DefineRule("throwing filter", r =>
            {
                var xs = r.Var<Set<Node>>("xs");
                r.When(xs.Is(t.Sets.Of()));
                r.Where(xs, _ => throw new InvalidOperationException("boom"));
            });
            t.Root.Assume(t.Sets.Of().Is(t.Of(3L, 2L)));

            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(rules));
            Assert.Equal("xs=(set-of #0 #1)", e.Bindings);
        }

        [Fact]
        public void ASplitOverASetOfClassesIsRefused()
        {
            var t = new Theory();
            var domain = t.Graph.DefineFunction<long, Set<Node>>("domain");
            var exception = Assert.Throws<RuleDefinitionException>(() => t.Graph.DefineSplit("cases", s =>
            {
                var (k, d) = s.Vars<long, Set<Node>>("k", "d");
                s.When(d.Is(domain.Of(k)));
                var v = s.Cases("v", d);
                s.Assume(domain.Of(k).Is(Primitives.Singleton<Node>().Of(v)));
            }));
            Assert.Contains("holds classes", exception.Message);
        }

    }

}
