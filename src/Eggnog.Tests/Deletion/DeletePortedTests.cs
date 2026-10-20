using Xunit;

namespace Eggnog.Tests.Deletion
{

    /// <summary>
    /// Deletion, ported from egglog's tests (<c>tests/delete.egg</c>, <c>tests/repro-new-backend-delete.egg</c> and the
    /// deletion in <c>tests/fail_wrong_assertion.egg</c> at 5e655cae): a deleted row is matched by no later rule and
    /// found by no check, and a rule may delete the row it matched.
    /// </summary>
    public sealed class DeletePortedTests
    {

        /// <summary>
        /// tests/delete.egg: after <c>(delete (foo 1))</c>, a rule that matches <c>(foo 1)</c> never fires (egglog's
        /// rule panics; this one writes a fact that must not appear).
        /// </summary>
        [Fact]
        public void ADeletedRowIsMatchedByNoRule()
        {
            var graph = new EGraph();
            var foo = graph.DefineFunction<long, long>("foo");
            var hit = graph.DefineRelation<long>("hit");
            var rules = graph.DefineRuleset("rules");
            graph.Root.Assume(foo.Of(1L).Is(7L));
            Assert.True(graph.Root.Check(foo.Of(1L).Is(7L)));

            graph.Root.Delete(foo.Of(1L));
            rules.DefineRule("foo 1 was there", r =>
            {
                var x = r.Var<long>("x");
                r.When(x.Is(foo.Of(1L)));
                r.Then(hit.Of(x));
            });
            graph.Root.Run(Schedule.Repeat(1, Schedule.Run(rules)));

            Assert.Equal(0, graph.Root.CountRows(hit));
        }

        /// <summary>
        /// tests/repro-new-backend-delete.egg: a rule over the nullary relation <c>(A)</c> deletes the row it matched.
        /// </summary>
        [Fact]
        public void ARuleDeletesTheRowItMatched()
        {
            var graph = new EGraph();
            var a = graph.DefineRelation("A");
            var rules = graph.DefineRuleset("rules");
            graph.Root.Assume(a.Of());
            rules.DefineRule("delete A", r =>
            {
                r.When(a.Of());
                r.Delete(a.Of());
            });

            graph.Root.Run(Schedule.Repeat(1, Schedule.Run(rules)));

            Assert.False(graph.Root.Check(a.Of()));
        }

        /// <summary>
        /// tests/fail_wrong_assertion.egg: after <c>(delete (f 1))</c> of a min lattice's row, its value no longer checks.
        /// </summary>
        [Fact]
        public void ADeletedValueNoLongerChecks()
        {
            var graph = new EGraph();
            var f = graph.DefineFunction<long, long>("f", ValueLattice.Min<long>());
            graph.Root.Assume(f.Of(1L).Is(4L));
            graph.Root.Assume(f.Of(1L).Is(5L));
            Assert.True(graph.Root.Check(f.Of(1L).Is(4L)));
            Assert.False(graph.Root.Check(f.Of(1L).Is(2L)));

            graph.Root.Delete(f.Of(1L));

            Assert.False(graph.Root.Check(f.Of(1L).Is(4L)));
        }

    }

}
