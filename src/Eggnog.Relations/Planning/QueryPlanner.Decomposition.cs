using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal static partial class QueryPlanner
    {

        /// <summary>
        /// Picks the next variable to eliminate and returns its neighbourhood closed under the functional dependencies.
        /// egglog's <c>next_var_to_eliminate</c> (core-relations/src/free_join/plan.rs:412-471).
        /// </summary>
        /// <remarks>
        /// For each live variable in ascending order: its neighbourhood <c>N</c> is every variable of every working atom
        /// holding it; the key is (number of working atoms touching <c>N</c>, the joined <see cref="ColumnUniqueness"/>
        /// of its occurrences in real atoms). The first minimum wins, so ties go to the lowest variable id (Rust's
        /// <c>min_by_key</c>). As in egglog, the count uses <c>N</c> without the closure (plan.rs:438 has it commented
        /// out) while the returned bag is the closure, which may name variables already eliminated.
        /// </remarks>
        /// <param name="vars">The live variables.</param>
        /// <param name="atoms">The working atoms, synthetic covering atoms included.</param>
        /// <param name="ctx">The original query, for dependencies and statistics.</param>
        /// <returns>The bag's variables as a set, or <c>null</c> when no variable is left.</returns>
        static FixedBitSet? NextVariableToEliminate(DenseIdMap<Variable, VarInfo> vars, DenseIdMap<AtomId, QueryAtom> atoms, PlanningContext ctx)
        {
            var capacity = ctx.VariableCapacity;
            FixedBitSet? best = null;
            var bestOcc = 0;
            var bestSize = default(ColumnUniqueness);
            foreach (var (variable, info) in vars)
            {
                var neighbourhood = new FixedBitSet(capacity);
                foreach (var (_, atom) in atoms)
                {
                    if (atom.ContainsVariable(variable) == false)
                        continue;

                    foreach (var (_, v) in atom.VarColumns)
                        neighbourhood.Insert(v.Index);
                }

                var occ = 0;
                foreach (var (_, atom) in atoms)
                {
                    foreach (var (_, v) in atom.VarColumns)
                    {
                        if (neighbourhood.Contains(v.Index))
                        {
                            occ++;
                            break;
                        }
                    }
                }

                var size = ColumnUniqueness.Identity;
                foreach (var sub in info.Occurrences)
                {
                    var atom = atoms[sub.Atom];
                    if (atom.IsDummy)
                        continue;

                    size = size.Join(ctx.Statistics.GetColumnUniqueness(atom.Table, atom.GetColumn(variable)));
                }

                if (best is null || occ < bestOcc || (occ == bestOcc && size.CompareTo(bestSize) < 0))
                {
                    best = neighbourhood;
                    bestOcc = occ;
                    bestSize = size;
                }
            }

            return best is null ? null : ctx.FunDeps.Closure(best);
        }

        /// <summary>
        /// Removes a bag from the working hypergraph. egglog's <c>update_hypergraph</c>
        /// (core-relations/src/free_join/plan.rs:473-539): the covering variables (those of the bag still occurring in
        /// an atom that reaches outside it) are found first; every atom inside the bag is removed, together with its
        /// occurrences, and variables left without occurrences die; a synthetic atom over the covering variables is
        /// added in their place.
        /// </summary>
        /// <param name="bag">The bag's variables, ascending.</param>
        /// <param name="bagSet">The same, as a set.</param>
        /// <param name="vars">The live variables; updated.</param>
        /// <param name="atoms">The working atoms; updated.</param>
        static void UpdateHypergraph(List<Variable> bag, FixedBitSet bagSet, DenseIdMap<Variable, VarInfo> vars, DenseIdMap<AtomId, QueryAtom> atoms)
        {
            var covering = new List<Variable>();
            foreach (var variable in bag)
            {
                if (vars.TryGetValue(variable, out var info) == false)
                    continue;

                foreach (var occ in info.Occurrences)
                {
                    if (HasVariableOutside(atoms[occ.Atom], bagSet))
                    {
                        covering.Add(variable);
                        break;
                    }
                }
            }

            var removed = new List<AtomId>();
            atoms.Retain((id, atom) =>
            {
                if (HasVariableOutside(atom, bagSet))
                    return true;

                removed.Add(id);
                return false;
            });

            foreach (var variable in bag)
            {
                if (vars.TryGetValue(variable, out var info) == false)
                    continue;

                info.Occurrences.RemoveAll(occ => removed.Contains(occ.Atom));
                if (info.Occurrences.Count == 0)
                    vars.Take(variable);
            }

            var varColumns = new VarColumnMap();
            for (var i = 0; i < covering.Count; i++)
                varColumns.Insert(covering[i], new ColumnId(i), out _);

            var fake = atoms.Push(new QueryAtom(TableId.Dummy, varColumns, [], []));
            for (var i = 0; i < covering.Count; i++)
                vars[covering[i]].Occurrences.Add(new SubAtom(fake, [new ColumnId(i)]));
        }

        static bool HasVariableOutside(QueryAtom atom, FixedBitSet set)
        {
            foreach (var (_, v) in atom.VarColumns)
                if (set.Contains(v.Index) == false)
                    return true;

            return false;
        }

        /// <summary>
        /// Tree decomposition by variable elimination, then pruning. egglog's <c>decompose_into_bags</c>
        /// (core-relations/src/free_join/plan.rs:541-702).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each elimination step yields a bag: every original atom with a variable in the eliminated neighbourhood,
        /// whole, and the neighbourhood's variables with their occurrences restricted to those atoms.
        /// </para>
        /// <para>
        /// Pruning repeats until nothing changes. Subsumption: each bag absorbs every earlier kept bag whose variables
        /// contain or are contained in its own, and takes the later position. Ears: a bag one of whose atoms determines
        /// all its variables (under the dependencies), or with exactly one atom entirely inside it (egglog's "HACK"), is
        /// merged with the bag sharing the most variables with it (ties to the lowest index); the merged bag takes the
        /// higher index, so higher-indexed bags stay heavier and closer to the root.
        /// </para>
        /// </remarks>
        /// <param name="original">The whole query.</param>
        /// <returns>The bags.</returns>
        internal static List<PlanningContext> DecomposeIntoBags(PlanningContext original)
        {
            var atoms = original.Atoms.Clone();
            var vars = PlanningContext.CloneVariables(original.Variables);
            foreach (var (variable, info) in original.Variables)
                if (info.Occurrences.Count == 0)
                    vars.Take(variable);

            var bags = new List<PlanningContext>();
            while (NextVariableToEliminate(vars, atoms, original) is { } bagSet)
            {
                var bag = new List<Variable>();
                for (var v = 0; v < bagSet.Length; v++)
                    if (bagSet.Contains(v))
                        bag.Add(new Variable(v));

                UpdateHypergraph(bag, bagSet, vars, atoms);

                var subAtoms = new DenseIdMap<AtomId, QueryAtom>();
                foreach (var (id, atom) in original.Atoms)
                {
                    foreach (var (_, v) in atom.VarColumns)
                    {
                        if (bagSet.Contains(v.Index))
                        {
                            subAtoms.Insert(id, atom);
                            break;
                        }
                    }
                }

                var subVars = new DenseIdMap<Variable, VarInfo>();
                foreach (var variable in bag)
                {
                    var info = original.Variables[variable].Clone();
                    info.Occurrences.RemoveAll(occ => subAtoms.ContainsKey(occ.Atom) == false);
                    subVars.Insert(variable, info);
                }

                bags.Add(new PlanningContext(subVars, subAtoms, original.FunDeps, original.Statistics, original.ApproximateSizes, original.VariableCapacity));
            }

            foreach (var (_, atom) in atoms)
                if (atom.IsDummy == false && atom.HasNoVariables == false)
                    throw new InvalidOperationException("All atoms should be put into bags.");

            var changed = true;
            while (changed)
            {
                changed = false;

                // Pruning 1: merge bags subsumed by others.
                var pruned = new List<PlanningContext>(bags.Count);
                foreach (var bag1 in bags)
                {
                    var k = 0;
                    while (k < pruned.Count)
                    {
                        var bag2 = pruned[k];
                        if (bag1.IsSubsumedBy(bag2) || bag2.IsSubsumedBy(bag1))
                        {
                            bag1.MergeBag(bag2);
                            changed = true;
                            pruned.RemoveAt(k);
                        }
                        else
                        {
                            k++;
                        }
                    }

                    pruned.Add(bag1);
                }

                bags = pruned;

                // Pruning 2: merge ears into the bag they share the most variables with.
                var i = 0;
                while (i < bags.Count)
                {
                    if (IsEar(bags[i], original) == false)
                    {
                        i++;
                        continue;
                    }

                    // Rust's max_by_key over descending indexes keeps the last maximum: the lowest index.
                    var j = -1;
                    var count = -1;
                    for (var k = bags.Count - 1; k >= 0; k--)
                    {
                        if (k == i)
                            continue;

                        var c = bags[k].CountCommonVariables(bags[i]);
                        if (c >= count)
                        {
                            count = c;
                            j = k;
                        }
                    }

                    if (j < 0 || count == 0)
                    {
                        i++;
                        continue;
                    }

                    // Invariant: bigger-numbered bags are heavier and stay at the root of the tree.
                    if (i < j)
                    {
                        var ear = bags[i];
                        bags.RemoveAt(i);
                        bags[j - 1].MergeBag(ear);
                    }
                    else
                    {
                        var parent = bags[j];
                        bags.RemoveAt(j);
                        bags[i - 1].MergeBag(parent);
                    }

                    changed = true;
                }
            }

            return bags;
        }

        /// <summary>
        /// Gets whether a bag is an ear. egglog's <c>is_ear</c> closure (core-relations/src/free_join/plan.rs:651-665).
        /// </summary>
        static bool IsEar(PlanningContext bag, PlanningContext original)
        {
            var vars = new List<Variable>();
            foreach (var (_, atom) in bag.Atoms)
            {
                vars.Clear();
                foreach (var (_, v) in atom.VarColumns)
                    vars.Add(v);

                if (bag.IsSubsumedBy(original.FunDeps.Closure(CollectionsMarshal.AsSpan(vars), original.VariableCapacity)))
                    return true;
            }

            var inside = 0;
            foreach (var (_, atom) in bag.Atoms)
                if (bag.HasAllVariablesOf(atom))
                    inside++;

            return inside == 1;
        }

    }

}
