using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// One column of one atom scanned by an <see cref="IntersectStage"/>, with the atom's slow constraints when this is
    /// the atom's first scan in plan order. egglog's <c>SingleScanSpec</c> (core-relations/src/free_join/plan.rs:76-81).
    /// </summary>
    internal sealed class SingleScanSpec
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <param name="column">The column; the first column holding the stage's variable.</param>
        /// <param name="constraints">The slow constraints to apply; owned by the new instance.</param>
        public SingleScanSpec(AtomId atom, ColumnId column, Constraint[] constraints)
        {
            ArgumentNullException.ThrowIfNull(constraints);
            Atom = atom;
            Column = column;
            Constraints = constraints;
        }

        /// <summary>
        /// Gets the atom.
        /// </summary>
        public AtomId Atom { get; }

        /// <summary>
        /// Gets the column.
        /// </summary>
        public ColumnId Column { get; }

        /// <summary>
        /// Gets the slow constraints to apply to the atom's rows; empty unless this is the atom's first scan. Callers
        /// must not modify the array.
        /// </summary>
        public Constraint[] Constraints { get; }

        /// <summary>
        /// Formats as <c>a0.c1</c>, followed by <c>{constraints}</c> when there are any.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(Atom).Append('.').Append(Column);
            PlanText.AppendConstraints(b, Constraints);
            return b.ToString();
        }

    }

}
