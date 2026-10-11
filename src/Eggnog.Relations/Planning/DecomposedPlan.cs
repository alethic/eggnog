using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// A Yannakakis-style plan from a tree (path) decomposition: egglog's <c>DecomposedPlan</c>
    /// (core-relations/src/free_join/plan.rs:283-290).
    /// </summary>
    /// <remarks>
    /// <see cref="Blocks"/> run in order, leaf first; block <c>i</c> writes materialization <c>MatId(i)</c>, and the
    /// executor skips the run when a materialization comes out empty. The last block has no message variables.
    /// <see cref="ResultBlock"/> then re-expands the value variables, starting with a <see cref="MatScanMode.Full"/>
    /// scan of the last block when that block has value variables, and feeds the action.
    /// </remarks>
    internal sealed class DecomposedPlan : PlanTemplate
    {

        /// <summary>
        /// Initializes a new instance. The arrays and the atom map are owned by the new instance.
        /// </summary>
        /// <param name="atoms">The query's atoms.</param>
        /// <param name="variableCount">The number of variable slots of the query.</param>
        /// <param name="headers">The header specs.</param>
        /// <param name="existenceChecks">The atoms without variables to check.</param>
        /// <param name="blocks">The materializing blocks, leaf first.</param>
        /// <param name="resultBlock">The stages that read the materializations and feed the action.</param>
        /// <param name="action">The action the plan feeds.</param>
        public DecomposedPlan(DenseIdMap<AtomId, QueryAtom> atoms, int variableCount, JoinHeaderSpec[] headers, ExistenceCheck[] existenceChecks, PlanBlock[] blocks, JoinStage[] resultBlock, ActionId action) :
            base(PlanKind.Decomposed, atoms, variableCount, headers, existenceChecks, action)
        {
            ArgumentNullException.ThrowIfNull(blocks);
            ArgumentNullException.ThrowIfNull(resultBlock);
            Blocks = blocks;
            ResultBlock = resultBlock;
        }

        /// <summary>
        /// Gets the materializing blocks, leaf first. Callers must not modify the array.
        /// </summary>
        public PlanBlock[] Blocks { get; }

        /// <summary>
        /// Gets the result block. Callers must not modify the array.
        /// </summary>
        public JoinStage[] ResultBlock { get; }

        /// <summary>
        /// Formats the plan, one header, check, block spec or stage per line.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append("DecomposedPlan").AppendLine();
            foreach (var h in Headers)
                b.Append("  header ").Append(h).AppendLine();
            foreach (var c in ExistenceChecks)
                b.Append("  ").Append(c).AppendLine();
            for (var i = 0; i < Blocks.Length; i++)
            {
                b.Append("  block ").Append(i).Append(' ').Append(Blocks[i].Spec).AppendLine();
                var stages = Blocks[i].Stages;
                for (var j = 0; j < stages.Length; j++)
                    b.Append("    [").Append(j).Append("] ").Append(stages[j]).AppendLine();
            }

            b.Append("  result").AppendLine();
            for (var j = 0; j < ResultBlock.Length; j++)
                b.Append("    [").Append(j).Append("] ").Append(ResultBlock[j]).AppendLine();

            return b.ToString();
        }

    }

}
