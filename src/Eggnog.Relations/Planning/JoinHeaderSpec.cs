using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// The fast constraints of one atom, evaluated into a row subset before the join starts: egglog's
    /// <c>JoinHeader</c> (core-relations/src/free_join/plan.rs:93-131) without the subset.
    /// </summary>
    /// <remarks>
    /// egglog stores the subset in the header, which ties a plan to one table state. A template stores only the
    /// constraints; <see cref="PlanTemplate.Instantiate{TEvaluator}"/> evaluates them per run, exactly as egglog
    /// re-processes the headers of a cached plan on every instantiation (core-relations/src/query.rs:174-189).
    /// </remarks>
    internal sealed class JoinHeaderSpec
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <param name="constraints">The fast constraints; owned by the new instance.</param>
        public JoinHeaderSpec(AtomId atom, Constraint[] constraints)
        {
            ArgumentNullException.ThrowIfNull(constraints);
            Atom = atom;
            Constraints = constraints;
        }

        /// <summary>
        /// Gets the atom.
        /// </summary>
        public AtomId Atom { get; }

        /// <summary>
        /// Gets the fast constraints. Callers must not modify the array.
        /// </summary>
        public Constraint[] Constraints { get; }

        /// <summary>
        /// Formats as <c>a0{c1 == 3}</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(Atom);
            PlanText.AppendConstraints(b, Constraints);
            return b.ToString();
        }

    }

}
