using Xunit;

namespace Eggnog.Tests.Readme
{

    /// <summary>
    /// The code examples of the repository's <c>README.md</c> and the package's <c>src/Eggnog/README.md</c>, verbatim,
    /// each followed by assertions of what the README says it shows. Edit an example here and in the README together.
    /// </summary>
    public sealed class ReadmeExampleTests
    {

        /// <summary>
        /// The quick start of both READMEs: rewrite rules over arithmetic terms, saturation, and extraction of the
        /// cheapest equivalent term. The sort it uses is <see cref="Expr"/>.
        /// </summary>
        [Fact]
        public void QuickStart()
        {
            var graph = new EGraph();
            graph.DefineSort<Expr>("Expr");
            var num = graph.DefineConstructor<long, Expr>("Num");
            var name = graph.DefineConstructor<string, Expr>("Var");
            var add = graph.DefineConstructor<Expr, Expr, Expr>("Add");
            var mul = graph.DefineConstructor<Expr, Expr, Expr>("Mul");

            var rules = graph.DefineRuleset("arith");
            rules.DefineRule("commute add", r =>
            {
                var (a, b) = r.Vars<Expr, Expr>("a", "b");
                r.Rewrite(add.Of(a, b), add.Of(b, a));
            });
            rules.DefineRule("times one", r =>
            {
                var a = r.Var<Expr>("a");
                r.Rewrite(mul.Of(a, num.Of(1L)), a);
            });
            rules.DefineRule("fold add", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.Rewrite(add.Of(num.Of(x), num.Of(y)), num.Of(Primitives.Add.Of(x, y)));
            });

            var root = graph.Root;
            Expr e = root.Make(add.Of(mul.Of(name.Of("x"), num.Of(1L)), add.Of(num.Of(2L), num.Of(3L))));
            root.Run(Schedule.Saturate(rules));

            bool same = root.AreEqual(e, add.Of(num.Of(5L), name.Of("x")));   // true
            ExtractedTerm best = root.Extract(e);                              // (Add (Var "x") (Num 5))

            Assert.True(same);
            Assert.Equal("(Add (Var \"x\") (Num 5))", best.ToString());
        }

        /// <summary>
        /// The repository README's second example: a frozen base shared by queries, a query scope per question, and a
        /// case split on a domain whose every case proves the answer, which the lift brings into the query.
        /// </summary>
        [Fact]
        public void ScopesAndCaseSplits()
        {
            var graph = new EGraph();
            var signal = graph.DefineRelation<string>("signal");
            var color = graph.DefineFunction<string, ValueSet<string>>("color", ValueLattice.Intersect<string>());
            var stop = graph.DefineRelation<string>("stop");

            var rules = graph.DefineRuleset("traffic");
            foreach (var c in new[] { "red", "amber" })
            {
                rules.DefineRule($"{c} means stop", r =>
                {
                    var s = r.Var<string>("s");
                    r.When(signal.Of(s), color.Of(s).Is(ValueSet.Of(c)));
                    r.Then(stop.Of(s));
                });
            }

            // One case per color a signal can still show; each case assumes color(s) = {that color}.
            var byColor = graph.DefineDomainSplit("by color", color, [signal]);
            var schedule = Schedule.Saturate(rules, Schedule.Split(byColor, rules));

            graph.Root.Assume(signal.Of("north"));
            graph.Root.Freeze();                       // a shared base: read-only, children on any thread

            using var q1 = graph.Root.CreateChild();
            q1.Assume(color.Of("north").Is(ValueSet.Of("red", "amber")));
            q1.Run(schedule);
            bool mustStop = q1.Check(stop.Of("north"));            // true: both cases prove it

            using var q2 = graph.Root.CreateChild();
            q2.Assume(color.Of("north").Is(ValueSet.Of("red", "green")));
            q2.Run(schedule);
            bool mayGo = q2.Check(stop.Of("north")) == false;      // true: the green case does not prove it

            bool atRoot = graph.Root.Check(stop.Of("north"));      // false: what a query learns stays in it

            Assert.True(mustStop);
            Assert.True(mayGo);
            Assert.False(atRoot);
        }

    }

}
