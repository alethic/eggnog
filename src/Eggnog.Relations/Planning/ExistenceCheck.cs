using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// An atom with no variables (every entry a constant or a wildcard) that the executor must check once, before the
    /// join: some visible row of the atom's header subset satisfies <see cref="SlowConstraints"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fix (map §8.1 row 2, core-relations/src/free_join/plan.rs:1502-1505): no stage ever scans such an atom, so egglog
    /// only checks that its header subset is non-empty. That subset may hold tombstones, and the atom's slow
    /// constraints (an equality on an uncacheable column, a range on an unsorted one) are never applied, so a deleted or
    /// non-matching fact still fires the rule. The template lists these atoms explicitly so the executor checks for a
    /// visible row that satisfies every constraint.
    /// </para>
    /// <para>
    /// Fix (core-relations/src/free_join/plan.rs:498-506, 591-596): tree decomposition drops atoms without variables
    /// from every bag, so a decomposed egglog plan loses even their headers. Here they keep their headers and checks in
    /// both plan kinds.
    /// </para>
    /// </remarks>
    internal sealed class ExistenceCheck
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <param name="slowConstraints">Its slow constraints; owned by the new instance.</param>
        public ExistenceCheck(AtomId atom, Constraint[] slowConstraints)
        {
            ArgumentNullException.ThrowIfNull(slowConstraints);
            Atom = atom;
            SlowConstraints = slowConstraints;
        }

        /// <summary>
        /// Gets the atom.
        /// </summary>
        public AtomId Atom { get; }

        /// <summary>
        /// Gets the slow constraints a row must also satisfy. Callers must not modify the array.
        /// </summary>
        public Constraint[] SlowConstraints { get; }

        /// <summary>
        /// Formats as <c>exists a0{c1 &gt; 2}</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append("exists ").Append(Atom);
            PlanText.AppendConstraints(b, SlowConstraints);
            return b.ToString();
        }

    }

}
