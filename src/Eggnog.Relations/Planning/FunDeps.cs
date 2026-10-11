using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The functional dependencies of a query: for each atom, its key-column variables determine its value-column
    /// variables. egglog's <c>FunDeps</c> (core-relations/src/query.rs:952-1002).
    /// </summary>
    /// <remarks>
    /// Tree decomposition treats variables determined by the ones already in a bag as free: the bag chosen when a
    /// variable is eliminated is the closure of its neighbourhood (core-relations/src/free_join/plan.rs:465-470), and
    /// an atom whose key closure covers a bag makes the bag an ear (plan.rs:651-655). Built once per query, then
    /// read-only.
    /// </remarks>
    internal sealed class FunDeps
    {

        readonly List<(Variable[] Antecedent, Variable[] Consequent)> dependencies = new();
        int variableBound;

        /// <summary>
        /// Gets the number of dependencies.
        /// </summary>
        public int Count => dependencies.Count;

        /// <summary>
        /// Gets one more than the highest variable index mentioned by any dependency.
        /// </summary>
        public int VariableBound => variableBound;

        /// <summary>
        /// Gets dependency <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The index, in insertion order.</param>
        /// <returns>The antecedent and consequent; callers must not modify the arrays.</returns>
        public (Variable[] Antecedent, Variable[] Consequent) this[int index] => dependencies[index];

        /// <summary>
        /// Adds <paramref name="antecedent"/> → <paramref name="consequent"/>. A dependency with an empty antecedent is
        /// trivial and dropped, as egglog does. The arrays are owned by this instance afterwards.
        /// </summary>
        /// <param name="antecedent">The determining variables.</param>
        /// <param name="consequent">The determined variables.</param>
        public void AddDependency(Variable[] antecedent, Variable[] consequent)
        {
            ArgumentNullException.ThrowIfNull(antecedent);
            ArgumentNullException.ThrowIfNull(consequent);
            if (antecedent.Length == 0)
                return;

            foreach (var v in antecedent)
                Track(v);
            foreach (var v in consequent)
                Track(v);

            dependencies.Add((antecedent, consequent));
        }

        /// <summary>
        /// Computes every variable determined by <paramref name="seed"/>: the seed plus, repeatedly, the consequent of
        /// each dependency whose antecedent is entirely known, until nothing changes. egglog's <c>closure</c>.
        /// O(dependencies × fixpoint rounds × arity).
        /// </summary>
        /// <param name="seed">The known variables; not modified.</param>
        /// <returns>A new set holding the closure, at least as long as <paramref name="seed"/>.</returns>
        public FixedBitSet Closure(FixedBitSet seed)
        {
            ArgumentNullException.ThrowIfNull(seed);
            var result = new FixedBitSet(Math.Max(seed.Length, variableBound));
            for (var i = 0; i < seed.Length; i++)
                if (seed.Contains(i))
                    result.Insert(i);

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (antecedent, consequent) in dependencies)
                {
                    var known = true;
                    foreach (var v in antecedent)
                    {
                        if (result.Contains(v.Index) == false)
                        {
                            known = false;
                            break;
                        }
                    }

                    if (known == false)
                        continue;

                    foreach (var v in consequent)
                        if (result.Insert(v.Index))
                            changed = true;
                }
            }

            return result;
        }

        /// <summary>
        /// Computes the closure of <paramref name="variables"/> (see <see cref="Closure(FixedBitSet)"/>).
        /// </summary>
        /// <param name="variables">The known variables.</param>
        /// <param name="capacity">A lower bound on the length of the result, typically the query's variable count.</param>
        /// <returns>A new set holding the closure.</returns>
        public FixedBitSet Closure(ReadOnlySpan<Variable> variables, int capacity)
        {
            var bound = capacity;
            foreach (var v in variables)
                bound = Math.Max(bound, v.Index + 1);

            var seed = new FixedBitSet(bound);
            foreach (var v in variables)
                seed.Insert(v.Index);

            return Closure(seed);
        }

        void Track(Variable v)
        {
            if (v.IsPlaceholder || v.Index < 0)
                throw new ArgumentException("A functional dependency may only mention real variables.");

            variableBound = Math.Max(variableBound, v.Index + 1);
        }

    }

}
