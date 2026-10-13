using System.Collections.Generic;
using System.Diagnostics;

namespace Eggnog
{

    /// <summary>
    /// A split step: one step of the run's cached <c>SplitDriver</c> for this node in the scope, which finds the requests
    /// of its split rules, opens the cases of those worth taking, runs <see cref="InCase"/> in them with nested splits
    /// disabled, lifts (lift before deepening), then runs it again with nested splits enabled and lifts once more if that
    /// changed something.
    /// </summary>
    /// <remarks>
    /// The step is skipped in phase A of an enclosing split (nested splits disabled) and in a contradictory scope (every
    /// case would be vacuous). It counts as a step of the run for the limits, and reports progress only when it
    /// opened cases or a lift changed the scope, so a <c>Saturate</c> around it ends once its cases have nothing more to
    /// give. Whether it changed the top-level scope, for <see cref="RunReport.Changed"/>, is whether that scope's
    /// <see cref="Scope.Version"/> moved over the step: opening cases alone changes nothing there. When the step ends, a
    /// lattice merge that threw while the engine caught one of the run's cases under the scope up, and that nothing took
    /// since, fails the run.
    /// </remarks>
    internal sealed class SplitSchedule : Schedule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rules">The split rules, in order: each one's position is its requests' key source.</param>
        /// <param name="inCase">The schedule run in each case.</param>
        public SplitSchedule(SplitRule[] rules, Schedule inCase)
        {
            Rules = rules;
            InCase = inCase;
        }

        /// <summary>
        /// Gets the split rules, in order.
        /// </summary>
        public SplitRule[] Rules { get; }

        /// <summary>
        /// Gets the schedule run in each case.
        /// </summary>
        public Schedule InCase { get; }

        /// <inheritdoc />
        internal override bool Execute(ScheduleExecutor executor, Scope scope, int depth, bool inCase, bool splitsEnabled)
        {
            if (splitsEnabled == false)
                return false;

            var engine = scope.Engine;
            engine.EnsureClean();
            if (engine.IsStopped || engine.IsContradictory)
                return false;

            executor.BeforeStep(scope);
            executor.SyncSplitMirrors(scope, Rules);
            var driver = executor.DriverFor(this);
            var top = ReferenceEquals(scope, executor.Top);
            var version = top ? scope.Version : 0;
            var report = new Relations.SplitReport();
            var started = Stopwatch.GetTimestamp();
            bool changed;
            executor.BeginSplitStep(scope, report);
            try
            {
                changed = driver.Step(engine.Context, depth, report);
            }
            catch (Relations.PanicException panic)
            {
                // A lift's flush recorded a failure in the scope.
                throw scope.FaultWith(panic);
            }
            finally
            {
                executor.EndSplitStep();
            }

            // The engine catches each case up inside the step (a lift's catch-up of every case, the vacuity check after it)
            // and records, without throwing, a lattice merge that throws there. A record no later iteration of the case took
            // fails the run here, before the cases are disposed with it.
            if (executor.TakeCaseCatchUpFailure(scope) is { } failure)
                throw failure;

            if (top)
            {
                // Progress ends a Saturate; a change is what the step's lifts wrote into the scope.
                executor.Statistics.LastTopStepChanged = changed;
                executor.Statistics.TopChanged |= scope.Version != version;
            }

            executor.RecordStep(RunStepKind.SplitStep, scope, changed, Stopwatch.GetElapsedTime(started), null, report.CasesOpened);
            return changed;
        }

        /// <inheritdoc />
        internal override void Collect(ISet<Ruleset> rulesets, ICollection<Fact> conditions, ISet<SplitRule> splits)
        {
            foreach (var rule in Rules)
                splits.Add(rule);

            InCase.Collect(rulesets, conditions, splits);
        }

    }

}
