using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A lattice merge that throws while the engine catches a case up inside a split step of a run (N114): the lift writes
    /// the cases' join into the scope, catches every case up with it, and records a case's refused merge without throwing.
    /// The run takes that record, when a case is about to run again or when the step ends, and fails with a
    /// <see cref="RuleEvaluationException"/> that faults the case and the run's scope (N94), whatever the in-case schedule
    /// and the depth limit.
    /// </summary>
    public sealed class SplitStepCatchUpFaultTests
    {

        /// <summary>
        /// Splits <c>k</c> over <c>{a, b}</c> in <see cref="ThrowingLatticeWorld.Parent"/>: each case picks a value and its
        /// rule writes <c>f(x) = weight(v)</c> (1 for a, 3 for b) and <c>done(x)</c>; the lift writes the join
        /// <c>f(x) = 1</c> into the scope, and case b's catch-up merges (3, 1).
        /// </summary>
        /// <param name="untilDone">Whether the in-case schedule is <c>Until(Saturate(rules), done(x))</c>, which runs no
        /// iteration once the case has run, rather than <c>Saturate(rules)</c>.</param>
        static (ThrowingLatticeWorld World, Schedule Schedule) Build(bool untilDone)
        {
            var w = new ThrowingLatticeWorld((old, incoming) => old != incoming);
            var dom = w.Graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var weight = w.Graph.DefineFunction<string, long>("weight");
            var pick = w.Graph.DefineRelation<string, string>("pick");
            var done = w.Graph.DefineRelation<string>("done");
            var rules = w.Graph.DefineRuleset("rules");
            rules.DefineRule("weigh the pick", r =>
            {
                var (k, v) = r.Vars<string, string>("k", "v");
                var n = r.Var<long>("n");
                r.When(pick.Of(k, v), weight.Of(v).Is(n));
                r.Then(w.F.Of("x").Is(n), done.Of("x"));
            });
            var split = w.Graph.DefineSplit("pick a value", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(pick.Of(p, v));
            });
            w.Parent.Assume(dom.Of("k").Is(ValueSet.Of("a", "b")), weight.Of("a").Is(1L), weight.Of("b").Is(3L));
            var inCase = untilDone ? Schedule.Until(Schedule.Saturate(Schedule.Run(rules)), done.Of("x")) : Schedule.Saturate(Schedule.Run(rules));
            return (w, Schedule.Split(split, inCase));
        }

        /// <summary>
        /// The refused merge fails the run with the lattice's record (its name, the delegate's exception inside); the run's
        /// scope is faulted with that exception, and the run's cases are gone.
        /// </summary>
        /// <param name="depthLimit">The split depth limit: 1 runs nothing in the cases after the lift, so the step's end
        /// takes the record; above 1 phase B's entry into each case takes it.</param>
        /// <param name="untilDone">Whether the in-case schedule runs no iteration once the case has run.</param>
        [Theory]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(4, false)]
        [InlineData(4, true)]
        public void ACasesCatchUpMergeFailureFailsTheRun(int depthLimit, bool untilDone)
        {
            var (w, schedule) = Build(untilDone);
            w.Arm();

            var failure = Assert.Throws<RuleEvaluationException>(() => w.Parent.Run(schedule, new RunOptions { SplitDepthLimit = depthLimit }));

            Assert.Contains("throwing max", failure.Message);
            Assert.Equal("merge refused", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
            Assert.Same(failure, w.Parent.Failure);
            Assert.Empty(w.Parent.Children);
        }

        /// <summary>
        /// Without the refusal the same run completes and lifts the join, and nothing is faulted: the check of the cases'
        /// records reports only what a merge recorded.
        /// </summary>
        /// <param name="depthLimit">The split depth limit.</param>
        /// <param name="untilDone">Whether the in-case schedule runs no iteration once the case has run.</param>
        [Theory]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(4, false)]
        [InlineData(4, true)]
        public void TheSameRunWithoutTheRefusalCompletes(int depthLimit, bool untilDone)
        {
            var (w, schedule) = Build(untilDone);

            var report = w.Parent.Run(schedule, new RunOptions { SplitDepthLimit = depthLimit });

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.False(w.Parent.IsFaulted);
            Assert.True(w.Parent.Check(w.F.Of("x").Is(1L)));
        }

    }

}
