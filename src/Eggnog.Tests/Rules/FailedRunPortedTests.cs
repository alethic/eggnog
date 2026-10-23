using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// What a failed run keeps, as egglog's: a panic stops the failing match's actions and the run's join, the batches of
    /// matches already formed still run, each match keeping what it wrote before a call of its own failed; the run's
    /// progress counts, so a later run applies neither those matches again nor the matches of a semi-naive rule it did
    /// not reach; and the e-graph goes on, as after <c>(fail (run 1))</c>. Each program ran on egglog's binary at 5e655cae as the comments say.
    /// </summary>
    public sealed class FailedRunPortedTests
    {

        [Fact]
        public void EachMatchOfTheBatchKeepsWhatItWroteBeforeItsCallFailed()
        {
            // (datatype Math (Num i64) (Lit i64)) (relation R (i64)) (function k (i64) i64 :no-merge) (R 1) (R 2) (R 3)
            // (rule ((R x)) ((Num x) (set (k x) x) (Lit (vec-get (vec-of 1) (- x 2))))) (fail (run 1))
            // (check (Num 1)) (check (Num 2)) (check (Num 3)) (check (= (k 1) 1)) (check (= (k 2) 2)) (check (= (k 3) 3))
            // (check (Lit 1))
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var lit = graph.DefineConstructor<long, Node>("Lit");
            var r = graph.DefineRelation<long>("R");
            var k = graph.DefineFunction<long, long>("k");
            graph.Values.RegisterVec<long>();
            var root = graph.Root;
            root.Assume(r.Of(1L), r.Of(2L), r.Of(3L));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("r", b =>
            {
                var x = b.Var<long>("x");
                b.When(r.Of(x));
                b.Then(Fact.Exists(num.Of(x)), k.Of(x).Is(x), Fact.Exists(lit.Of(Primitives.VecGet<long>().Of(Primitives.VecOf<long>().Of(1L), Primitives.Subtract.Of(x, 2L)))));
            });

            Assert.Throws<RuleEvaluationException>(() => root.Run(rules));
            Assert.False(root.IsFaulted);
            foreach (var x in new[] { 1L, 2L, 3L })
            {
                Assert.True(root.Check(Fact.Exists(num.Of(x))));
                Assert.Equal(x, root.Get(k.Of(x)));
            }

            Assert.True(root.Check(Fact.Exists(lit.Of(1L))));
            Assert.Equal(1, root.CountRows(lit));
        }

        [Fact]
        public void AFailedRunsMatchesAreNotAppliedAgain()
        {
            // (relation R (i64)) (function cnt () i64 :merge (+ old new)) (R 1) (R 2)
            // (rule ((R x)) ((set (cnt) 1) (Lit (vec-get (vec-of 1 2) (- x 2))))) (fail (run 1)) (check (= (cnt) 2))
            // (run 1) (check (= (cnt) 2)) (R 3) (run 1) (check (= (cnt) 3)) (check (Lit 2))
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var lit = graph.DefineConstructor<long, Node>("Lit");
            var r = graph.DefineRelation<long>("R");
            var cnt = graph.DefineFunction("cnt", FunctionMerge.Expression<long>(static (old, @new) => Primitives.Add.Of(old, @new)));
            graph.Values.RegisterVec<long>();
            var root = graph.Root;
            root.Assume(r.Of(1L), r.Of(2L));
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("r", b =>
            {
                var x = b.Var<long>("x");
                b.When(r.Of(x));
                b.Then(cnt.Of().Is(1L), Fact.Exists(lit.Of(Primitives.VecGet<long>().Of(Primitives.VecOf<long>().Of(1L, 2L), Primitives.Subtract.Of(x, 2L)))));
            });

            Assert.Throws<RuleEvaluationException>(() => root.Run(rules));
            Assert.Equal(2L, root.Get(cnt.Of()));
            Assert.True(root.Check(Fact.Exists(lit.Of(1L))));

            root.Run(rules);
            Assert.Equal(2L, root.Get(cnt.Of()));

            root.Assume(r.Of(3L));
            root.Run(rules);
            Assert.Equal(3L, root.Get(cnt.Of()));
            Assert.True(root.Check(Fact.Exists(lit.Of(2L))));
        }

        [Fact]
        public void AFailedRunAppliesOnlyTheBatchItFormed()
        {
            // (datatype Math (Num i64) (Lit i64)) (relation R (i64)) (R 1) ... (R 1000)
            // (rule ((R x)) ((Num x) (Lit (/ 10 (- x 3))))) (fail (run 1)) (print-size Num) (print-size Lit) (run 1)
            // (print-size Num) (print-size Lit): 128, 8, 128, 8. The failure at x = 3 stops the join after the first
            // batch of 128 bindings, which runs: every binding writes its Num, every other its Lit (8 values). The matches
            // the join did not reach are not applied by the next run either.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var num = graph.DefineConstructor<long, Node>("Num");
            var lit = graph.DefineConstructor<long, Node>("Lit");
            var r = graph.DefineRelation<long>("R");
            var root = graph.Root;
            root.Write(w =>
            {
                for (var x = 1L; x <= 1000; x++)
                    w.Assume(r.Of(x));
            });
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("r", b =>
            {
                var x = b.Var<long>("x");
                b.When(r.Of(x));
                b.Then(Fact.Exists(num.Of(x)), Fact.Exists(lit.Of(Primitives.Divide.Of(10L, Primitives.Subtract.Of(x, 3L)))));
            });

            Assert.Throws<RuleEvaluationException>(() => root.Run(rules));
            Assert.Equal(128, root.CountRows(num));
            Assert.Equal(8, root.CountRows(lit));

            root.Run(rules);
            Assert.Equal(128, root.CountRows(num));
            Assert.Equal(8, root.CountRows(lit));
        }

        [Fact]
        public void ARuleAfterAFailingOneIsNotReached()
        {
            // (datatype Math (Num i64) (Lit i64)) (relation R (i64)) (relation S (i64)) (R 1) ... (R 1000)
            // (rule ((R x)) ((Lit (/ 10 (- x 3)))) :name "bad") (rule ((R x)) ((S x)) :name "good") (fail (run 1))
            // (print-size S) (run 1) (print-size S): 0, 0. The failure stops the run before the rule after it, whose
            // matches count as run.
            var graph = new EGraph();
            graph.DefineSort<Node>("Math");
            var lit = graph.DefineConstructor<long, Node>("Lit");
            var r = graph.DefineRelation<long>("R");
            var s = graph.DefineRelation<long>("S");
            var root = graph.Root;
            root.Write(w =>
            {
                for (var x = 1L; x <= 1000; x++)
                    w.Assume(r.Of(x));
            });
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("bad", b =>
            {
                var x = b.Var<long>("x");
                b.When(r.Of(x));
                b.Then(Fact.Exists(lit.Of(Primitives.Divide.Of(10L, Primitives.Subtract.Of(x, 3L)))));
            });
            rules.DefineRule("good", b =>
            {
                var x = b.Var<long>("x");
                b.When(r.Of(x));
                b.Then(s.Of(x));
            });

            Assert.Throws<RuleEvaluationException>(() => root.Run(rules));
            Assert.Equal(0, root.CountRows(s));

            root.Run(rules);
            Assert.Equal(0, root.CountRows(s));
        }

    }

}
