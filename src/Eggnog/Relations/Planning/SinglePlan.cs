using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// A plan that evaluates the whole query with one stage list: egglog's <c>SinglePlan</c>
    /// (core-relations/src/free_join/plan.rs:248-254).
    /// </summary>
    internal sealed class SinglePlan : PlanTemplate
    {

        /// <summary>
        /// Initializes a new instance. The arrays and the atom map are owned by the new instance.
        /// </summary>
        /// <param name="atoms">The query's atoms.</param>
        /// <param name="variableCount">The number of variable slots of the query.</param>
        /// <param name="headers">The header specs.</param>
        /// <param name="existenceChecks">The atoms without variables to check.</param>
        /// <param name="stages">The stages.</param>
        /// <param name="action">The action the plan feeds.</param>
        public SinglePlan(DenseIdMap<AtomId, QueryAtom> atoms, int variableCount, JoinHeaderSpec[] headers, ExistenceCheck[] existenceChecks, JoinStage[] stages, ActionId action) :
            base(PlanKind.Single, atoms, variableCount, headers, existenceChecks, action)
        {
            ArgumentNullException.ThrowIfNull(stages);
            Stages = stages;
        }

        /// <summary>
        /// Gets the stages, in static order (the executor reorders them by current sizes). Callers must not modify the
        /// array.
        /// </summary>
        public JoinStage[] Stages { get; }

        /// <summary>
        /// Formats the plan, one header, check or stage per line.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append("SinglePlan").AppendLine();
            foreach (var h in Headers)
                b.Append("  header ").Append(h).AppendLine();
            foreach (var c in ExistenceChecks)
                b.Append("  ").Append(c).AppendLine();
            for (var i = 0; i < Stages.Length; i++)
                b.Append("  [").Append(i).Append("] ").Append(Stages[i]).AppendLine();

            return b.ToString();
        }

    }

}
