using System;

namespace Eggnog.Relations
{

    internal sealed partial class JoinRun
    {

        /// <summary>
        /// Runs one rule serially, sending its matches to <paramref name="actions"/>: egglog's per-plan body of the serial
        /// <c>run_rule_set</c> (core-relations/src/free_join/execute.rs:510-605). A decomposed plan fills each block's
        /// materialization in order (an empty one ends the rule), then runs the result block.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <param name="actions">The action batches, without a scope.</param>
        public void RunRuleSerial(RulePlan rule, ActionBuffer actions)
        {
            ArgumentNullException.ThrowIfNull(rule);
            var sink = new InPlaceActionSink(actions);
            if (BeginRule(rule))
            {
                switch (rule.Template)
                {
                    case SinglePlan single:
                        RunJoinStages(single.Stages, rule.Action.Index, ref sink);
                        break;
                    case DecomposedPlan decomposed:
                        RunDecomposedSerial(decomposed, rule.Action.Index, ref sink);
                        break;
                }
            }

            EndRule();
        }

        /// <summary>
        /// Runs one rule as a task of a parallel run, sending its matches to <paramref name="actions"/>: egglog's rule
        /// task in the parallel <c>run_rule_set</c> (core-relations/src/free_join/execute.rs:376-493). Drains at
        /// positions 0 and 1 fork tasks into the actions' scope; each block of a decomposed plan runs in a scope of its
        /// own that this task waits for (helping) before its materialization is merged.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <param name="actions">The rule task's action batches, with the run's scope.</param>
        public void RunRuleParallel(RulePlan rule, ActionBuffer actions)
        {
            ArgumentNullException.ThrowIfNull(rule);
            var sink = new ScopedActionSink(actions);
            if (BeginRule(rule))
            {
                switch (rule.Template)
                {
                    case SinglePlan single:
                        RunJoinStages(single.Stages, rule.Action.Index, ref sink);
                        break;
                    case DecomposedPlan decomposed:
                        RunDecomposedParallel(decomposed, rule.Action.Index, ref sink);
                        break;
                }
            }

            EndRule();
        }

        /// <summary>
        /// The serial decomposed-plan driver.
        /// </summary>
        void RunDecomposedSerial(DecomposedPlan plan, int action, ref InPlaceActionSink sink)
        {
            var blocks = plan.Blocks;
            var specs = GetSpecs(blocks);
            EnsureMaterializations(blocks.Length);
            var building = new Materialization[blocks.Length];
            for (var i = 0; i < blocks.Length; i++)
                building[i] = CreateMaterialization(specs[i]);

            var materializer = new InPlaceMaterializerSink(building, specs);
            for (var i = 0; i < blocks.Length; i++)
            {
                RunJoinStages(blocks[i].Stages, i, ref materializer);

                // egglog checks emptiness only. After an early stop its next block would sort stages over nodes the
                // interrupted block left moved out; stop here instead.
                if (building[i].IsEmpty || signals.ShouldStop)
                {
                    for (var j = i; j < blocks.Length; j++)
                        building[j].Dispose();

                    return;
                }

                materializations[i] = building[i];
            }

            RunJoinStages(plan.ResultBlock, action, ref sink);
        }

        /// <summary>
        /// The parallel decomposed-plan driver: egglog's nested <c>in_place_scope</c> per block with a
        /// <c>ScopedMaterializer</c> (core-relations/src/free_join/execute.rs:412-479).
        /// </summary>
        void RunDecomposedParallel(DecomposedPlan plan, int action, ref ScopedActionSink sink)
        {
            var blocks = plan.Blocks;
            var specs = GetSpecs(blocks);
            EnsureMaterializations(blocks.Length);
            var building = new ConcurrentMaterialization[blocks.Length];
            for (var i = 0; i < blocks.Length; i++)
            {
                var spec = specs[i];
                building[i] = new ConcurrentMaterialization(spec.MessageVariables.Length, Math.Max(1, spec.ValueVariables.Length));
            }

            for (var i = 0; i < blocks.Length; i++)
            {
                var scope = new WorkScope(db.Parallelism.DegreeOfParallelism);
                var materializer = new ScopedMaterializerSink(scope, building, specs);
                RunJoinStages(blocks[i].Stages, i, ref materializer);
                scope.Wait();
                var merged = building[i].Merge();
                building[i].Dispose();
                if (merged.IsEmpty || signals.ShouldStop)
                {
                    for (var j = i + 1; j < blocks.Length; j++)
                        building[j].Dispose();

                    return;
                }

                materializations[i] = merged;
            }

            RunJoinStages(plan.ResultBlock, action, ref sink);
        }

        /// <summary>
        /// Gets the spec of each block.
        /// </summary>
        static MatSpec[] GetSpecs(PlanBlock[] blocks)
        {
            var specs = new MatSpec[blocks.Length];
            for (var i = 0; i < blocks.Length; i++)
                specs[i] = blocks[i].Spec;

            return specs;
        }

        /// <summary>
        /// Creates an empty materialization shaped by <paramref name="spec"/>: one stale cell per row when the block has
        /// no value variables (core-relations/src/free_join/execute.rs:2143-2152).
        /// </summary>
        static Materialization CreateMaterialization(MatSpec spec) => new(spec.MessageVariables.Length, Math.Max(1, spec.ValueVariables.Length));

        /// <summary>
        /// Makes room for <paramref name="count"/> materializations.
        /// </summary>
        void EnsureMaterializations(int count)
        {
            if (materializations.Length < count)
                materializations = new Materialization?[count];
        }

    }

}
