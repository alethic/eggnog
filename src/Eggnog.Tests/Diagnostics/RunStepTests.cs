using System.Collections.Generic;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Diagnostics
{

    /// <summary>
    /// The steps of a run: what a <see cref="RunStep"/> holds, and that it cannot be changed.
    /// </summary>
    [Collection(Relations.Tests.AllocationProbeCollection.Name)]
    public sealed class RunStepTests
    {

        /// <summary>
        /// <see cref="RunStep"/> is immutable: an observer that downcasts its <c>Rules</c> finds no list it may clear,
        /// so the step <see cref="RunReport.Steps"/> keeps is unchanged.
        /// </summary>
        [Fact]
        public void RunStepRulesCannotBeMutatedThroughACast()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("grow", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 3L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            t.Root.Assume(t.Num.Of(0L));
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                RecordSteps = true,
                Observer = step =>
                {
                    if (step.Rules is IList<RuleStatistics> { IsReadOnly: false } list)
                        list.Clear();
                },
            });

            Assert.Single(report.Steps[0].Rules);
        }

    }

}
