using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// egglog's <c>vec-union</c>, the write primitive of a vector of an eq sort: a port of tests/vec.egg at 5e655cae,
    /// lines 45-62, and where it runs and does not.
    /// </summary>
    public sealed class VecUnionTests
    {

        sealed class Theory
        {

            public Theory()
            {
                Graph.DefineSort<Node>("X");
                Graph.Values.RegisterVec<Node>();
                A = Graph.DefineConstructor<Node>("a");
                B = Graph.DefineConstructor<Node>("b");
                P = Graph.DefineFunction<Vec<Node>>("p");
                Q = Graph.DefineFunction<Vec<Node>>("q");
                Root.Assume(P.Of().Is(Primitives.VecOf<Node>().Of(A.Of())), Q.Of().Is(Primitives.VecOf<Node>().Of(B.Of())));
            }

            public EGraph Graph { get; } = new();

            public Scope Root => Graph.Root;

            public Constructor<Node> A { get; }

            public Constructor<Node> B { get; }

            public Function<Vec<Node>> P { get; }

            public Function<Vec<Node>> Q { get; }

            public bool Merged => Root.AreEqual(Root.Make(A.Of()), Root.Make(B.Of()));

        }

        [Fact]
        public void AnActionUnionsTheVectorsClasses()
        {
            // (push) (vec-union $p $q) (check (= $p $q)) (check (= (a) (b))) (pop): the union run for its effect alone.
            var t = new Theory();
            Assert.False(t.Root.Check(t.P.Of().Is(t.Q.Of())));

            t.Root.Assume(Fact.Exists(Primitives.VecUnion<Node>().Of(t.P.Of(), t.Q.Of())));

            Assert.True(t.Root.Check(t.P.Of().Is(t.Q.Of())));
            Assert.True(t.Merged);
        }

        [Fact]
        public void AWriteUnionsTheVectorsClasses()
        {
            // The union written as a value: (set (u) (vec-union $p $q)).
            var t = new Theory();
            var u = t.Graph.DefineFunction<Vec<Node>>("u");
            t.Root.Assume(u.Of().Is(Primitives.VecUnion<Node>().Of(t.P.Of(), t.Q.Of())));

            Assert.True(t.Root.Check(t.P.Of().Is(t.Q.Of())));
            Assert.True(t.Merged);
            Assert.True(t.Root.Check(u.Of().Is(t.P.Of())));
        }

        [Fact]
        public void VectorsOfTwoLengthsHaveNoUnion()
        {
            // (fail (vec-union (vec-of (a)) (vec-of (b) (a)))).
            var t = new Theory();
            var of = Primitives.VecOf<Node>();
            Assert.Throws<RuleEvaluationException>(() =>
                t.Root.Assume(Fact.Exists(Primitives.VecUnion<Node>().Of(of.Of(t.A.Of()), of.Of(t.B.Of(), t.A.Of())))));
            Assert.False(t.Merged);
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public void AMergeOfVectorsUnionsThem()
        {
            // (function ff () VX :merge (vec-union old new)) (set (ff) $p) (set (ff) $q) (check (= $p $q)) (check (= (a) (b))).
            var t = new Theory();
            var ff = t.Graph.DefineFunction<Vec<Node>>("ff", FunctionMerge.Expression<Vec<Node>>((old, @new) => Primitives.VecUnion<Node>().Of(old, @new)));
            t.Root.Assume(ff.Of().Is(t.P.Of()));
            t.Root.Assume(ff.Of().Is(t.Q.Of()));

            Assert.True(t.Root.Check(t.P.Of().Is(t.Q.Of())));
            Assert.True(t.Merged);
        }

        [Fact]
        public void ARuleHeadUnionsTheVectorsClasses()
        {
            var t = new Theory();
            var u = t.Graph.DefineFunction<Vec<Node>>("u");
            var rules = t.Graph.DefineRuleset("rules");
            var rule = rules.DefineRule("union", r =>
            {
                var (x, y) = r.Vars<Vec<Node>, Vec<Node>>("x", "y");
                r.When(x.Is(t.P.Of()), y.Is(t.Q.Of()));
                r.Then(u.Of().Is(Primitives.VecUnion<Node>().Of(x, y)));
            });
            Assert.Equal("(rule ((= x (p)) (= y (q))) ((set (u) (vec-union x y))) :ruleset rules)", rule.ToString());

            t.Root.Run(Schedule.Run(rules));

            Assert.True(t.Merged);
            Assert.True(t.Root.Check(u.Of().Is(t.Q.Of())));
        }

        [Fact]
        public void AUnionThatConflictsIsRefusedBeforeItIsWritten()
        {
            // (function g (X) i64 :no-merge) (set (g (a)) 1) (set (g (b)) 2): the union of a and b would give g two values.
            var t = new Theory();
            var g = t.Graph.DefineFunction<Node, long>("g");
            var u = t.Graph.DefineFunction<Vec<Node>>("u");
            t.Root.Assume(g.Of(t.A.Of()).Is(1L), g.Of(t.B.Of()).Is(2L));

            Assert.Throws<FactConflictException>(() => t.Root.Assume(u.Of().Is(Primitives.VecUnion<Node>().Of(t.P.Of(), t.Q.Of()))));

            Assert.False(t.Root.IsFaulted);
            Assert.False(t.Merged);
            Assert.Equal(0, t.Root.CountRows(u));
        }

        [Fact]
        public void AMergeOfVectorsIsSimulatedByTheBatch()
        {
            // The vec-union merge's unions are the batch's: a conflict they make is refused, and so are two lengths,
            // before anything is written; egglog's command errs the same ways.
            var t = new Theory();
            var g = t.Graph.DefineFunction<Node, long>("g");
            var ff = t.Graph.DefineFunction<Vec<Node>>("ff", FunctionMerge.Expression<Vec<Node>>((old, @new) => Primitives.VecUnion<Node>().Of(old, @new)));
            t.Root.Assume(g.Of(t.A.Of()).Is(1L), g.Of(t.B.Of()).Is(2L), ff.Of().Is(t.P.Of()));

            Assert.Throws<FactConflictException>(() => t.Root.Assume(ff.Of().Is(t.Q.Of())));
            var of = Primitives.VecOf<Node>();
            Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(ff.Of().Is(of.Of(t.B.Of(), t.A.Of()))));

            Assert.False(t.Root.IsFaulted);
            Assert.False(t.Merged);
            Assert.True(t.Root.Check(ff.Of().Is(t.P.Of())));
        }

        [Fact]
        public void AFactsUnionsTakeEffectAfterIt()
        {
            // (set (u) (vec-union (vec-union (p) (q)) (vec-of (c)))) and (set (ff) (vec-union (p) (q))): egglog applies the
            // unions after the command, so nothing in it sees them.
            var t = new Theory();
            var c = t.Graph.DefineConstructor<Node>("c");
            var u = t.Graph.DefineFunction<Vec<Node>>("u");
            var union = Primitives.VecUnion<Node>();
            t.Root.Assume(u.Of().Is(union.Of(union.Of(t.P.Of(), t.Q.Of()), Primitives.VecOf<Node>().Of(c.Of()))));
            Assert.True(t.Merged);
            Assert.True(t.Root.AreEqual(t.Root.Make(t.A.Of()), t.Root.Make(c.Of())));

            var other = new Theory();
            var ff = other.Graph.DefineFunction<Vec<Node>>("ff", FunctionMerge.Expression<Vec<Node>>((old, @new) => Primitives.VecUnion<Node>().Of(old, @new)));
            other.Root.Assume(ff.Of().Is(Primitives.VecUnion<Node>().Of(other.P.Of(), other.Q.Of())));
            Assert.True(other.Merged);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AChildUnionsVectors(bool frozen)
        {
            // Probe vec-union-child.egg, which egglog at 5e655cae runs clean: lines 45-62 of vec.egg, each block in a
            // push of its own: (set (u) (vec-union (p) (q))), the bare (vec-union (p) (q)) and a rule's (set (u)
            // (vec-union x y)) each make (a) and (b) one there and (p) and (q) with them; two lengths have no union; and
            // ff, which merges with vec-union, unions them too. After each pop they are apart. A child does each, but
            // for ff: only the root writes a function whose merge is not a lattice (decisions row 2), so a child refuses
            // the write before anything is written. The vector of two the root holds, as a child makes none.
            var t = new Theory();
            var u = t.Graph.DefineFunction<Vec<Node>>("u");
            var two = t.Graph.DefineFunction<Vec<Node>>("two");
            var ff = t.Graph.DefineFunction<Vec<Node>>("ff", FunctionMerge.Expression<Vec<Node>>((old, @new) => Primitives.VecUnion<Node>().Of(old, @new)));
            var union = Primitives.VecUnion<Node>();
            var rules = t.Graph.DefineRuleset("rules");
            rules.DefineRule("union", r =>
            {
                var (x, y) = r.Vars<Vec<Node>, Vec<Node>>("x", "y");
                r.When(x.Is(t.P.Of()), y.Is(t.Q.Of()));
                r.Then(u.Of().Is(union.Of(x, y)));
            });
            t.Root.Assume(two.Of().Is(Primitives.VecOf<Node>().Of(t.B.Of(), t.A.Of())));
            var (a, b) = (t.Root.Make(t.A.Of()), t.Root.Make(t.B.Of()));
            if (frozen)
                t.Root.Freeze();

            bool Merged(Scope scope) => scope.AreEqual(a, b);

            using (var child = t.Root.CreateChild())
            {
                child.Assume(u.Of().Is(union.Of(t.P.Of(), t.Q.Of())));
                Assert.True(Merged(child));
                Assert.True(child.Check(t.P.Of().Is(t.Q.Of())));
                Assert.True(child.Check(u.Of().Is(t.P.Of())));
                child.Engine.Context.CheckInvariants();
            }

            Assert.False(Merged(t.Root));
            Assert.False(t.Root.Check(t.P.Of().Is(t.Q.Of())));
            using (var child = t.Root.CreateChild())
            {
                child.Assume(Fact.Exists(union.Of(t.P.Of(), t.Q.Of())));
                Assert.True(Merged(child));
                Assert.True(child.Check(t.P.Of().Is(t.Q.Of())));
            }

            using (var child = t.Root.CreateChild())
            {
                child.Run(Schedule.Run(rules));
                Assert.True(Merged(child));
                Assert.True(child.Check(u.Of().Is(t.Q.Of())));
            }

            using (var child = t.Root.CreateChild())
            {
                Assert.Throws<RuleEvaluationException>(() => child.Assume(Fact.Exists(union.Of(t.P.Of(), two.Of()))));
                Assert.False(Merged(child));
                var merge = Assert.Throws<InvalidOperationException>(() => child.Assume(ff.Of().Is(t.P.Of())));
                Assert.Contains("only the root may write it", merge.Message, StringComparison.Ordinal);
                Assert.False(child.IsFaulted);
                Assert.Equal(0, child.CountRows(ff));
            }

            Assert.False(Merged(t.Root));
            Assert.False(t.Root.Check(t.P.Of().Is(t.Q.Of())));
            Assert.Equal(0, t.Root.CountRows(u));
        }

        [Fact]
        public void ARuleDefinedDuringAChildsRunUnionsVectorsThere()
        {
            // Probe vec-union-later-rule.egg, which egglog at 5e655cae runs clean: in a push (run rules 1) of a ruleset
            // whose one rule writes (seen), then (rule ((= x (p)) (= y (q))) ((vec-union x y)) :ruleset rules) and (run
            // rules 1), which makes (a) and (b) one there, and (p) and (q) with them; after the pop they are apart. Here
            // the rule is defined by a run observer after the child's first iteration, and takes part in its second.
            var t = new Theory();
            var seen = t.Graph.DefineRelation("seen");
            var rules = t.Graph.DefineRuleset("rules");
            rules.DefineRule("seen", r =>
            {
                r.When(r.Var<Vec<Node>>("x").Is(t.P.Of()));
                r.Then(seen.Of());
            });
            var (a, b) = (t.Root.Make(t.A.Of()), t.Root.Make(t.B.Of()));
            t.Root.Freeze();
            var defined = false;
            void Observe(RunStep step)
            {
                if (step.Kind != RunStepKind.Iteration || defined)
                    return;

                defined = true;
                Assert.False(step.Scope.AreEqual(a, b));
                rules.DefineRule("union", r =>
                {
                    var (x, y) = r.Vars<Vec<Node>, Vec<Node>>("x", "y");
                    r.When(x.Is(t.P.Of()), y.Is(t.Q.Of()));
                    r.Then(Fact.Exists(Primitives.VecUnion<Node>().Of(x, y)));
                });
            }

            using (var child = t.Root.CreateChild())
            {
                child.Run(Schedule.Repeat(2, Schedule.Run(rules)), new RunOptions { Observer = Observe });
                Assert.True(defined);
                Assert.True(child.AreEqual(a, b));
                Assert.True(child.Check(t.P.Of().Is(t.Q.Of())));
            }

            Assert.False(t.Root.AreEqual(a, b));
        }

        [Fact]
        public void ASplitsCasesUnionVectors()
        {
            // A split whose in-case ruleset runs vec-union: each case is a child of the root, where the rule unions (a)
            // and (b), which the vectors hold, and the case's rebuild makes (p) and (q) one there; the lift carries (a)
            // = (b), which every case proves, to the root, whose rebuild then makes the vectors one. egglog has no split,
            // so the expected values are those of a fresh root running the rule's union itself (replay).
            foreach (var replay in new[] { false, true })
            {
                var t = new Theory();
                var choice = t.Graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
                var split = t.Graph.DefineSplit("pick", s =>
                {
                    var (k, d) = s.Vars<string, Set<long>>("k", "d");
                    s.When(d.Is(choice.Of(k)));
                    s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                    var v = s.Cases("v", d);
                    s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)));
                });
                var unions = t.Graph.DefineRuleset("unions");
                unions.DefineRule("union", r =>
                {
                    var (x, y) = r.Vars<Vec<Node>, Vec<Node>>("x", "y");
                    r.When(x.Is(t.P.Of()), y.Is(t.Q.Of()));
                    r.Then(Fact.Exists(Primitives.VecUnion<Node>().Of(x, y)));
                });
                t.Root.Assume(choice.Of("k").Is(Set.Of(1L, 2L)));

                t.Root.Run(replay ? Schedule.Run(unions) : Schedule.Split(split, Schedule.Run(unions)));

                Assert.False(t.Root.IsFaulted);
                Assert.True(t.Merged);
                Assert.True(t.Root.Check(t.P.Of().Is(t.Q.Of())));
                Assert.Empty(t.Root.Children);
                t.Root.Engine.Context.CheckInvariants();
            }
        }

        [Fact]
        public void ABodyAQueryAndAReadDoNotWrite()
        {
            // egglog rejects a write primitive in a rule's query.
            var t = new Theory();
            var union = Primitives.VecUnion<Node>();
            var rules = t.Graph.DefineRuleset("rules");
            var body = Assert.ThrowsAny<Exception>(() => rules.DefineRule("body", r =>
            {
                var (x, y, z) = r.Vars<Vec<Node>, Vec<Node>, Vec<Node>>("x", "y", "z");
                r.When(x.Is(t.P.Of()), y.Is(t.Q.Of()), z.Is(union.Of(x, y)));
                r.Then(t.P.Of().Is(z));
            }));
            Assert.Contains("writes tables, and a semi-naive rule's body neither reads nor writes", body.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => t.Root.Check(t.P.Of().Is(union.Of(t.P.Of(), t.Q.Of()))));
            Assert.Throws<InvalidOperationException>(() => union.TryInvoke(Vec<Node>.Empty, Vec<Node>.Empty, out _));
            Assert.False(t.Merged);
        }

    }

}
