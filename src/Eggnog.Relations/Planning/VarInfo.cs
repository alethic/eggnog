using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// What the planner knows about one query variable: egglog's <c>VarInfo</c>
    /// (core-relations/src/free_join/mod.rs:121-129).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Occurrences"/> holds one <see cref="SubAtom"/> per atom containing the variable, listing every column
    /// of that atom that holds it. The query builder appends them in atom-id order, so the first occurrence is the
    /// lowest-numbered atom, which is the generic-join cover (core-relations/src/free_join/plan.rs:1567). Merged bags
    /// may append occurrences out of order, exactly as egglog's <c>merge_bag</c> does.
    /// </para>
    /// <para>
    /// Mutable: the planner works on deep copies (<see cref="Clone"/>) and never changes the instances of a
    /// <see cref="Query"/>.
    /// </para>
    /// </remarks>
    internal sealed class VarInfo
    {

        /// <summary>
        /// Initializes a new instance with no occurrences.
        /// </summary>
        /// <param name="name">The variable's name, for diagnostics, or <c>null</c>.</param>
        public VarInfo(string? name = null)
        {
            Name = name;
            Occurrences = new List<SubAtom>();
        }

        VarInfo(VarInfo other)
        {
            Name = other.Name;
            UsedInRhs = other.UsedInRhs;
            DefinedInRhs = other.DefinedInRhs;
            Occurrences = new List<SubAtom>(other.Occurrences);
        }

        /// <summary>
        /// Gets the occurrences of the variable, one per atom.
        /// </summary>
        public List<SubAtom> Occurrences { get; }

        /// <summary>
        /// Gets or sets whether the action (right-hand side) reads the variable. The planner also sets it on its copies
        /// for variables a bag must materialize.
        /// </summary>
        public bool UsedInRhs { get; set; }

        /// <summary>
        /// Gets or sets whether the action defines the variable (it has no occurrence in the query).
        /// </summary>
        public bool DefinedInRhs { get; set; }

        /// <summary>
        /// Gets the variable's name, for diagnostics.
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// Creates a deep copy: the occurrence list is copied (the immutable sub-atoms are shared).
        /// </summary>
        /// <returns>The copy.</returns>
        public VarInfo Clone() => new(this);

        /// <summary>
        /// Gets the occurrence in <paramref name="atom"/>, if any. Linear in the number of occurrences.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <returns>The occurrence, or <c>null</c>.</returns>
        public SubAtom? FindOccurrence(AtomId atom)
        {
            foreach (var occ in Occurrences)
                if (occ.Atom == atom)
                    return occ;

            return null;
        }

    }

}
