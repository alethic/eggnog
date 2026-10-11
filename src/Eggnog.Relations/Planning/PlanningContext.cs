using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The planner's view of a query or of one bag of a tree decomposition: egglog's <c>PlanningContext</c>
    /// (core-relations/src/free_join/plan.rs:1210-1265).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bag holds every original atom that touches one of its variables, whole, so its atoms may mention variables
    /// that are not in <see cref="Variables"/> (for the rectangle query, eliminating <c>x</c> gives
    /// <c>R(x,y), S(y,z), T(z,-), U(-,x)</c>). Those columns are not expanded in the bag; every planning step skips
    /// them.
    /// </para>
    /// <para>
    /// The variable infos belong to this context (copies made by <see cref="Clone"/> or by the planner); the atoms,
    /// functional dependencies and statistics are shared and read-only.
    /// </para>
    /// </remarks>
    internal sealed class PlanningContext
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="variables">The variables, owned by this context.</param>
        /// <param name="atoms">The atoms.</param>
        /// <param name="funDeps">The query's functional dependencies.</param>
        /// <param name="statistics">The statistics source.</param>
        /// <param name="approximateSizes">The approximate size of each original atom, by atom index.</param>
        /// <param name="variableCapacity">The query's number of variable slots.</param>
        public PlanningContext(DenseIdMap<Variable, VarInfo> variables, DenseIdMap<AtomId, QueryAtom> atoms, FunDeps funDeps, IPlanStatistics statistics, long[] approximateSizes, int variableCapacity)
        {
            ArgumentNullException.ThrowIfNull(variables);
            ArgumentNullException.ThrowIfNull(atoms);
            ArgumentNullException.ThrowIfNull(funDeps);
            ArgumentNullException.ThrowIfNull(statistics);
            ArgumentNullException.ThrowIfNull(approximateSizes);
            Variables = variables;
            Atoms = atoms;
            FunDeps = funDeps;
            Statistics = statistics;
            ApproximateSizes = approximateSizes;
            VariableCapacity = variableCapacity;
        }

        /// <summary>
        /// Gets the variables of the context.
        /// </summary>
        public DenseIdMap<Variable, VarInfo> Variables { get; }

        /// <summary>
        /// Gets the atoms of the context.
        /// </summary>
        public DenseIdMap<AtomId, QueryAtom> Atoms { get; }

        /// <summary>
        /// Gets the query's functional dependencies.
        /// </summary>
        public FunDeps FunDeps { get; }

        /// <summary>
        /// Gets the statistics source.
        /// </summary>
        public IPlanStatistics Statistics { get; }

        /// <summary>
        /// Gets the approximate size of each original atom (egglog's <c>approx_size</c>), by atom index.
        /// </summary>
        public long[] ApproximateSizes { get; }

        /// <summary>
        /// Gets the query's number of variable slots, the length of variable bitsets.
        /// </summary>
        public int VariableCapacity { get; }

        /// <summary>
        /// Creates a context over the same atoms with copies of the variable infos (egglog's derived <c>Clone</c>,
        /// which deep-copies the variable map).
        /// </summary>
        /// <returns>The copy.</returns>
        public PlanningContext Clone() => new(CloneVariables(Variables), Atoms.Clone(), FunDeps, Statistics, ApproximateSizes, VariableCapacity);

        /// <summary>
        /// Deep-copies a variable map.
        /// </summary>
        /// <param name="variables">The map.</param>
        /// <returns>A map with copies of every variable info.</returns>
        public static DenseIdMap<Variable, VarInfo> CloneVariables(DenseIdMap<Variable, VarInfo> variables)
        {
            var copy = new DenseIdMap<Variable, VarInfo>(variables.IdCount);
            foreach (var (variable, info) in variables)
                copy.Insert(variable, info.Clone());

            return copy;
        }

        /// <summary>
        /// Gets whether every variable of this bag is in <paramref name="other"/>. egglog's <c>is_subsumed_by</c>.
        /// </summary>
        /// <param name="other">The other bag.</param>
        /// <returns><c>true</c> if subsumed.</returns>
        public bool IsSubsumedBy(PlanningContext other)
        {
            foreach (var (variable, _) in Variables)
                if (other.Variables.ContainsKey(variable) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Gets whether every variable of this bag is in <paramref name="set"/>. egglog's <c>is_subsumed_by_vars</c>.
        /// </summary>
        /// <param name="set">The variables, by index.</param>
        /// <returns><c>true</c> if subsumed.</returns>
        public bool IsSubsumedBy(FixedBitSet set)
        {
            foreach (var (variable, _) in Variables)
                if (set.Contains(variable.Index) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Merges <paramref name="other"/> into this bag. egglog's <c>merge_bag</c>: a variable already present gains the
        /// occurrences in atoms it did not have yet (appended in <paramref name="other"/>'s order); a new variable is
        /// copied; atoms are unioned.
        /// </summary>
        /// <param name="other">The bag to merge in; not modified.</param>
        public void MergeBag(PlanningContext other)
        {
            foreach (var (variable, info) in other.Variables)
            {
                if (Variables.TryGetValue(variable, out var mine))
                {
                    foreach (var occ in info.Occurrences)
                        if (mine.FindOccurrence(occ.Atom) is null)
                            mine.Occurrences.Add(occ);
                }
                else
                {
                    Variables.Insert(variable, info.Clone());
                }
            }

            foreach (var (atom, info) in other.Atoms)
                if (Atoms.ContainsKey(atom) == false)
                    Atoms.Insert(atom, info);
        }

        /// <summary>
        /// Counts the variables this bag shares with <paramref name="other"/>. egglog's
        /// <c>common_vars_with(..).count()</c>.
        /// </summary>
        /// <param name="other">The other bag.</param>
        /// <returns>The count.</returns>
        public int CountCommonVariables(PlanningContext other)
        {
            var n = 0;
            foreach (var (variable, _) in Variables)
                if (other.Variables.ContainsKey(variable))
                    n++;

            return n;
        }

        /// <summary>
        /// Gets whether every variable of <paramref name="atom"/> is in this bag (vacuously for an atom without
        /// variables). egglog's <c>has_vars(atom.vars())</c>.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <returns><c>true</c> if the bag holds them all.</returns>
        public bool HasAllVariablesOf(QueryAtom atom)
        {
            foreach (var (_, variable) in atom.VarColumns)
                if (Variables.ContainsKey(variable) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Gets the approximate size of an original atom.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <returns>The estimate.</returns>
        public long GetApproximateSize(AtomId atom) => ApproximateSizes[atom.Index];

    }

}
