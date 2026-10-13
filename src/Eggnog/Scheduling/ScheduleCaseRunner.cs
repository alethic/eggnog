using System;

namespace Eggnog
{

    /// <summary>
    /// Runs a split step's in-case schedule in one case (§6.9.3; the engine's <c>ICaseSchedule</c>, E8): the case's
    /// <see cref="Scope"/> (its context's runtime owner, made when the case assumed its element) executes the schedule as
    /// a case (<c>inCase</c>), with nested split steps enabled only in phase B.
    /// </summary>
    /// <remarks>
    /// Phase A runs the schedule with nested splits disabled, then the driver lifts; phase B runs it again in each live case
    /// with nested splits enabled, which recurses into a nested driver's step at the next depth (colors §11.3). The case is
    /// registered among the run's live cases, whose rows count towards <see cref="RunOptions.RowLimit"/> (D42). Each entry
    /// first records the lifts the driver made since the previous case ran (§6.11), then takes a failure the engine's
    /// catch-up of the case recorded and throws it as a <see cref="RuleEvaluationException"/> (N114). Exceptions
    /// propagate: limits, cancellation and evaluation failures end the step, and the run disposes the cases. One per split
    /// node and run; called on the run's thread.
    /// </remarks>
    internal sealed class ScheduleCaseRunner : Relations.ICaseSchedule
    {

        readonly ScheduleExecutor executor;
        readonly Schedule inCase;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="executor">The run.</param>
        /// <param name="inCase">The in-case schedule.</param>
        public ScheduleCaseRunner(ScheduleExecutor executor, Schedule inCase)
        {
            this.executor = executor;
            this.inCase = inCase;
        }

        /// <inheritdoc />
        public bool Run(Relations.EGraphContext @case, int depth, bool allowSplits)
        {
            var scope = @case.GetLocal(executor.Top.Graph.RuntimeSlot).Owner
                ?? throw new InvalidOperationException("A case has no rule-layer scope.");

            executor.ObserveLifts();
            executor.AddCase(scope);

            // The engine caught the case up just before (its set's RunEach in phase A, the lift before phase B) and records
            // a lattice merge that throws there without throwing (D31): it fails the run at once (N114).
            if (scope.Engine.TryTakeRecordedFailure(out var message))
                throw scope.FaultWith(new Relations.PanicException(message));

            try
            {
                return inCase.Execute(executor, scope, depth, true, allowSplits);
            }
            finally
            {
                executor.CaseRunEnded();
            }
        }

    }

}
