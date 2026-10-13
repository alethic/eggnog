using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scheduling
{

    /// <summary>
    /// <c>Until</c> conditions that a run cannot check: refused before any work, so the schedule's earlier steps write
    /// nothing, and an <c>Until</c> with no condition is refused when the schedule is built.
    /// </summary>
    [Collection(Relations.Tests.AllocationProbeCollection.Name)]
    public sealed class UntilConditionTests
    {

        /// <summary>
        /// An <c>Until</c> condition that no host check can test (a variable) is refused before the run writes, by
        /// <c>Schedule.Until</c> or by <c>Run</c>: a host read the scope cannot make is an
        /// <see cref="ArgumentException"/> with nothing written, and <c>Run</c> validates the schedule before any work.
        /// </summary>
        [Fact]
        public void AnUncheckableUntilConditionIsRefusedBeforeAnyWork()
        {
            var t = new NodeTheory();
            var seed = t.Graph.DefineRuleset("seed");
            seed.DefineRule("one", r => r.Then(t.Num.Of(1L)));
            Var<long> leaked = null!;
            t.Graph.DefineQuery("leak", q =>
            {
                leaked = q.Var<long>("v");
                q.When(t.Num.Of(leaked));
            });

            Schedule schedule;
            try
            {
                schedule = Schedule.Sequence(seed, Schedule.Until(t.Rules, leaked.Is(5L)));
            }
            catch (ArgumentException)
            {
                return;
            }

            Assert.Throws<ArgumentException>(() => t.Root.Run(schedule));
            Assert.False(t.Root.Check(t.Num.Of(1L)), "The run wrote num(1) before refusing its own Until condition.");
        }

        /// <summary>
        /// An <c>Until</c> condition over another graph's table is refused before any work, as another graph's ruleset
        /// is.
        /// </summary>
        [Fact]
        public void AnUntilConditionOfAnotherGraphIsRefusedBeforeAnyWork()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            var seed = t.Graph.DefineRuleset("seed");
            seed.DefineRule("one", r => r.Then(t.Num.Of(1L)));
            var schedule = Schedule.Sequence(seed, Schedule.Until(t.Rules, other.Num.Of(5L)));

            Assert.Throws<ArgumentException>(() => t.Root.Run(schedule));
            Assert.False(t.Root.Check(t.Num.Of(1L)), "The run wrote num(1) before refusing another graph's table.");
        }

        /// <summary>
        /// <c>Until</c> with no condition is refused: it would never run its body, so a forgotten condition would turn
        /// the step into a no-op that reports a saturated, completed run.
        /// </summary>
        [Fact]
        public void AnUntilWithoutAConditionIsRefused()
        {
            var t = new NodeTheory();
            Assert.Throws<ArgumentException>(() => Schedule.Until(t.Rules));
        }

    }

}
