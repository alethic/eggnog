using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Columns of one atom that a <see cref="FusedIntersectStage"/> or <see cref="FusedIntersectMatStage"/> scans or
    /// indexes, with the atom's slow constraints when this is the atom's first scan in plan order. egglog's
    /// <c>ScanSpec</c> (core-relations/src/free_join/plan.rs:69-74).
    /// </summary>
    internal sealed class ScanSpec
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="toIndex">The atom and the columns to scan or index.</param>
        /// <param name="constraints">The slow constraints to apply; owned by the new instance.</param>
        public ScanSpec(SubAtom toIndex, Constraint[] constraints)
        {
            ArgumentNullException.ThrowIfNull(toIndex);
            ArgumentNullException.ThrowIfNull(constraints);
            ToIndex = toIndex;
            Constraints = constraints;
        }

        /// <summary>
        /// Gets the atom and the columns to scan or index.
        /// </summary>
        public SubAtom ToIndex { get; }

        /// <summary>
        /// Gets the atom.
        /// </summary>
        public AtomId Atom => ToIndex.Atom;

        /// <summary>
        /// Gets the slow constraints to apply to the atom's rows. Callers must not modify the array.
        /// </summary>
        public Constraint[] Constraints { get; }

        /// <summary>
        /// Formats as <c>a1[c0,c1]</c>, followed by <c>{constraints}</c> when there are any.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(ToIndex);
            PlanText.AppendConstraints(b, Constraints);
            return b.ToString();
        }

    }

}
