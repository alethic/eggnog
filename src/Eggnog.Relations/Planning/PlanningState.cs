namespace Eggnog.Relations
{

    /// <summary>
    /// What stage planning has already used up in one bag: egglog's <c>PlanningState</c>
    /// (core-relations/src/free_join/plan.rs:1270-1300). It makes each variable bound by one stage and each atom's slow
    /// constraints emitted once, on the atom's first scan in plan order.
    /// </summary>
    internal sealed class PlanningState
    {

        readonly FixedBitSet usedVariables;
        readonly FixedBitSet constrainedAtoms;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="variableCount">The number of variable slots.</param>
        /// <param name="atomCount">The number of atom slots.</param>
        public PlanningState(int variableCount, int atomCount)
        {
            usedVariables = new FixedBitSet(variableCount);
            constrainedAtoms = new FixedBitSet(atomCount);
        }

        /// <summary>
        /// Marks <paramref name="variable"/> as bound by a stage.
        /// </summary>
        /// <param name="variable">The variable.</param>
        public void MarkVariableUsed(Variable variable) => usedVariables.Insert(variable.Index);

        /// <summary>
        /// Gets whether a stage already binds <paramref name="variable"/>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns><c>true</c> if bound.</returns>
        public bool IsVariableUsed(Variable variable) => usedVariables.Contains(variable.Index);

        /// <summary>
        /// Takes the slow constraints of <paramref name="atom"/> the first time it is scanned. egglog's
        /// <c>take_atom_constraints_if_new</c> (core-relations/src/free_join/plan.rs:1593-1604).
        /// </summary>
        /// <param name="atom">The atom's identifier.</param>
        /// <param name="info">The atom.</param>
        /// <returns>A copy of its slow constraints the first time, an empty array afterwards.</returns>
        public Constraint[] TakeAtomConstraintsIfNew(AtomId atom, QueryAtom info)
        {
            if (constrainedAtoms.Contains(atom.Index))
                return [];

            constrainedAtoms.Insert(atom.Index);
            return info.SlowConstraints.Length == 0 ? [] : (Constraint[])info.SlowConstraints.Clone();
        }

    }

}
