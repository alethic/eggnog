using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// A generic-join step: bind <see cref="Variable"/> to every value present in all of <see cref="Scans"/>.
    /// egglog's <c>JoinStage::Intersect</c> (core-relations/src/free_join/plan.rs:138-141).
    /// </summary>
    /// <remarks>
    /// The planner emits it when a stage binds one variable that occurs in more than one atom of the block, so it always
    /// has at least two scans; each scan reads the first column of its atom holding the variable (repeated columns are
    /// enforced by the atom's slow <see cref="ConstraintKind.Eq"/> constraints).
    /// </remarks>
    internal sealed class IntersectStage : JoinStage
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="variable">The variable bound.</param>
        /// <param name="scans">The scans to intersect; owned by the new instance.</param>
        public IntersectStage(Variable variable, SingleScanSpec[] scans) :
            base(JoinStageKind.Intersect)
        {
            ArgumentNullException.ThrowIfNull(scans);
            if (scans.Length == 0)
                throw new ArgumentException("An intersection needs at least one scan.", nameof(scans));

            Variable = variable;
            Scans = scans;
        }

        /// <summary>
        /// Gets the variable bound.
        /// </summary>
        public Variable Variable { get; }

        /// <summary>
        /// Gets the scans to intersect. Callers must not modify the array.
        /// </summary>
        public SingleScanSpec[] Scans { get; }

        /// <summary>
        /// Formats as <c>Intersect v0 [a0.c0, a2.c1]</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append("Intersect ").Append(Variable).Append(" [");
            PlanText.AppendJoined(b, Scans, ", ");
            return b.Append(']').ToString();
        }

    }

}
