using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// A body that equates two different constants, directly (<c>(= 1 2)</c>) or through a variable
    /// (<c>(= x 1) (= x 2)</c>), is accepted, as egglog accepts it, and matches nothing: a rule never fires, a query has
    /// no match, and a split rule opens no case. The expected answers are the egglog binary's at the engine's pin for the
    /// same programs.
    /// </summary>
    public sealed class ContradictingConstantsTests
    {

        /// <summary>
        /// egglog, after <c>(relation R (i64)) (R 1)</c> and the five rules below, run three times, prints only
        /// <c>(R 1)</c> and <c>(R 40)</c>: <c>(rule ((R x) (= x 1) (= x 2)) ((R 10)))</c>, <c>(rule ((= 1 2)) ((R
        /// 20)))</c>, <c>(rule ((R x) (= y 1) (= y 2)) ((R 30)))</c> and <c>(rule ((R x) (= x 1) (= y 2) (= x y)) ((R
        /// 50)))</c> never fire, and <c>(rule ((R x) (= x 1) (= 1 1)) ((R 40)))</c> does.
        /// </summary>
        [Fact]
        public void ARuleThatEquatesDifferentConstantsNeverFires()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            t.Rules.DefineRule("through a variable", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x), x.Is(1L), x.Is(2L));
                r.Then(t.Num.Of(10L));
            });
            t.Rules.DefineRule("constants", r =>
            {
                r.When(((Term<long>)1L).Is(2L));
                r.Then(t.Num.Of(20L));
            });
            t.Rules.DefineRule("through a variable no atom uses", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x), y.Is(1L), y.Is(2L));
                r.Then(t.Num.Of(30L));
            });
            t.Rules.DefineRule("equal constants", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x), x.Is(1L), ((Term<long>)1L).Is(1L));
                r.Then(t.Num.Of(40L));
            });
            t.Rules.DefineRule("two variables of different constants", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x), x.Is(1L), y.Is(2L), x.Is(y));
                r.Then(t.Num.Of(50L));
            });

            t.Root.Run(Schedule.Repeat(3, t.Rules));

            Assert.Equal(2L, t.Root.CountRows(t.Num));
            Assert.True(t.Root.Check(t.Num.Of(1L)));
            Assert.True(t.Root.Check(t.Num.Of(40L)));
        }

        /// <summary>
        /// Different interned constants, strings, compare as different too: <c>(rule ((S x) (= x "a") (= x "b")) ((S
        /// "c")))</c> never fires in egglog.
        /// </summary>
        [Fact]
        public void ARuleThatEquatesDifferentStringsNeverFires()
        {
            var t = new NodeTheory();
            var s = t.Graph.DefineRelation<string>("S");
            t.Root.Assume(s.Of("a"), s.Of("b"));
            t.Rules.DefineRule("strings", r =>
            {
                var x = r.Var<string>("x");
                r.When(s.Of(x), x.Is("a"), x.Is("b"));
                r.Then(s.Of("c"));
            });

            var report = t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.False(report.Changed);
            Assert.False(t.Root.Check(s.Of("c")));
        }

        /// <summary>
        /// egglog's <c>(check (= 1 2))</c>, <c>(check (R x) (= x 1) (= x 2))</c> and
        /// <c>(check (R x) (= x 1) (= y 2) (= x y))</c> fail, and their <c>(fail (check ...))</c> pass: a query of any of
        /// them has no match, and the host's check of <c>1 = 2</c> is false.
        /// </summary>
        [Fact]
        public void AQueryThatEquatesDifferentConstantsHasNoMatch()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L));
            var through = t.Graph.DefineQuery("through a variable", q =>
            {
                var x = q.Var<long>("x");
                q.When(t.Num.Of(x), x.Is(1L), x.Is(2L));
            });
            var constants = t.Graph.DefineQuery("constants", q => q.When(((Term<long>)1L).Is(2L)));
            var unified = t.Graph.DefineQuery("two variables of different constants", q =>
            {
                var (x, y) = q.Vars<long, long>("x", "y");
                q.When(t.Num.Of(x), x.Is(1L), y.Is(2L), x.Is(y));
            });
            var equal = t.Graph.DefineQuery("equal constants", q =>
            {
                var x = q.Var<long>("x");
                q.When(t.Num.Of(x), x.Is(1L), x.Is(1L));
            });

            Assert.False(t.Root.Any(through));
            Assert.Equal(0, t.Root.Count(through));
            Assert.False(t.Root.Any(constants));
            Assert.False(t.Root.Any(unified));
            Assert.Equal(1, t.Root.Count(equal));
            Assert.False(t.Root.Check(((Term<long>)1L).Is(2L)));
            Assert.True(t.Root.Check(((Term<long>)1L).Is(1L)));
        }

        /// <summary>
        /// A split rule's body is a rule's body, so one that equates different constants matches nothing and opens no
        /// case, and its assumptions are never written.
        /// </summary>
        [Fact]
        public void ASplitThatEquatesDifferentConstantsOpensNoCase()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            var chosen = graph.DefineRelation<string, string>("chosen");
            var rules = graph.DefineRuleset("rules");
            var split = graph.DefineSplit("choose", s =>
            {
                var (p, d, n) = s.Vars<string, Set<string>, long>("p", "d", "n");
                s.When(d.Is(dom.Of(p)), n.Is(1L), n.Is(2L));
                var v = s.Cases("v", d);
                s.Assume(chosen.Of(p, v));
            });
            graph.Root.Assume(dom.Of("one").Is(Set.Of("x")));

            var report = graph.Root.Run(Schedule.Split(split, rules));

            Assert.Equal(0, report.CasesOpened);
            Assert.False(graph.Root.Check(chosen.Of("one", "x")));
        }

    }

}
