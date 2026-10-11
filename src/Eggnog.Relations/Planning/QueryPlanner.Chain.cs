using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal static partial class QueryPlanner
    {

        /// <summary>
        /// Orders the bags into a chain (a path decomposition), merging every bag that does not continue it into its
        /// chain node. egglog's <c>topologically_sort_bags</c> (core-relations/src/free_join/plan.rs:704-825).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each bag's parent is the later bag sharing the most variables with it (ties to the lowest index). For each bag
        /// the continuation child is the one minimizing the chain's pathwidth (atom count of the widest bag), ties to the
        /// later child. A depth-first walk from the roots (highest index first) then makes each chain node absorb every
        /// child except its continuation, with their whole subtrees. The result is reversed: leaves first, root last.
        /// </para>
        /// <para>
        /// Fix (map §8.1 row 6, plan.rs:805, commit <c>7f4cbc49</c>): egglog iterates <c>all_children[1..]</c>, so when
        /// the first child is not the continuation it is neither absorbed nor continued; it later becomes the root of a
        /// separate chain that contributes through a <c>Lookup</c> epilogue, and the layout differs from the one the
        /// pathwidth scored. Here every child other than the continuation is absorbed.
        /// </para>
        /// <para>
        /// Fix (plan.rs:735-740, the second half of map §2.7 item 3): egglog scores a candidate continuation by
        /// unioning this bag's atoms with the subtrees of the <em>candidate's own</em> children
        /// (<c>all_children_list[*chain_child]</c>, whose <c>child != chain_child</c> filter is then vacuous), but the
        /// bags the walk absorbs into this chain node are the candidate's <em>siblings</em>. The pathwidth commit
        /// <c>7f4cbc49</c> iterated <c>all_children_list[i]</c>, the siblings; the "review feedback" refactor
        /// <c>d19135d6</c> swapped the index. With every sibling really absorbed (the row 6 fix), the swapped score can
        /// choose the widest layout and fold a whole subtree into one bag, losing the Yannakakis pruning. Here the score
        /// is this bag's atoms plus the subtrees of every sibling of the candidate, as in <c>7f4cbc49</c>, so it measures
        /// the chain node the walk actually builds. Any choice yields a valid chain; only plan quality depends on it.
        /// </para>
        /// </remarks>
        /// <param name="bags">The bags from <see cref="DecomposeIntoBags"/>.</param>
        /// <returns>The chain, leaves first.</returns>
        internal static List<PlanningContext> TopologicallySortBags(List<PlanningContext> bags)
        {
            var n = bags.Count;
            var atomCapacity = 0;
            foreach (var bag in bags)
                atomCapacity = Math.Max(atomCapacity, bag.Atoms.IdCount);

            var children = new List<int>[n];
            for (var i = 0; i < n; i++)
                children[i] = new List<int>();

            var bestPathwidth = new int[n];
            var full = new FixedBitSet[n];
            var choice = new int[n];
            Array.Fill(choice, -1);

            for (var i = 0; i < n; i++)
            {
                var fullI = AtomSet(bags[i], atomCapacity);
                foreach (var child in children[i])
                    fullI.UnionWith(full[child]);

                full[i] = fullI;
                bestPathwidth[i] = fullI.Count;
                foreach (var chainChild in children[i])
                {
                    // The chain node this bag becomes if chainChild continues: the bag plus its other children's whole
                    // subtrees (fix of plan.rs:735-740, see the remarks).
                    var score = AtomSet(bags[i], atomCapacity);
                    foreach (var sibling in children[i])
                        if (sibling != chainChild)
                            score.UnionWith(full[sibling]);

                    var s = Math.Max(score.Count, bestPathwidth[chainChild]);
                    if (s <= bestPathwidth[i])
                    {
                        bestPathwidth[i] = s;
                        choice[i] = chainChild;
                    }
                }

                // The parent: the later bag sharing the most variables, ties to the lowest index.
                var parent = -1;
                var parentCount = 0;
                for (var j = i + 1; j < n; j++)
                {
                    var count = bags[j].CountCommonVariables(bags[i]);
                    if (count > parentCount)
                    {
                        parentCount = count;
                        parent = j;
                    }
                }

                if (parent >= 0)
                    children[parent].Add(i);
            }

            var pending = new PlanningContext?[n];
            for (var i = 0; i < n; i++)
                pending[i] = bags[i];

            var topo = new List<PlanningContext>(n);
            var visited = new bool[n];

            // Stack entries: (bag, chain node it is absorbed into, or -1 for a new chain node).
            var stack = new Stack<(int Bag, int Into)>();

            // Start from the last bags: early bags are more likely to be leaves, and a leafy bag should not be a root.
            for (var i = n - 1; i >= 0; i--)
            {
                if (visited[i])
                    continue;

                stack.Push((i, -1));
                visited[i] = true;
                while (stack.Count > 0)
                {
                    var (id, into) = stack.Pop();
                    var bag = pending[id]!;
                    pending[id] = null;

                    if (into >= 0)
                    {
                        // Absorbed: so is every descendant, none of them starts a chain node.
                        topo[into].MergeBag(bag);
                        foreach (var child in children[id])
                        {
                            visited[child] = true;
                            stack.Push((child, into));
                        }

                        continue;
                    }

                    // A chain node: the continuation child goes on, every other child is absorbed here.
                    var self = topo.Count;
                    if (children[id].Count > 0)
                    {
                        foreach (var child in children[id])
                        {
                            if (child == choice[id])
                                continue;

                            visited[child] = true;
                            stack.Push((child, self));
                        }

                        visited[choice[id]] = true;
                        stack.Push((choice[id], -1));
                    }

                    topo.Add(bag);
                }
            }

            topo.Reverse();
            return topo;
        }

        static FixedBitSet AtomSet(PlanningContext bag, int capacity)
        {
            var set = new FixedBitSet(capacity);
            foreach (var (atom, _) in bag.Atoms)
                set.Insert(atom.Index);

            return set;
        }

        /// <summary>
        /// Counts the bags each variable occurs in. egglog's <c>count_variable_usage_per_bag</c>
        /// (core-relations/src/free_join/plan.rs:827-842).
        /// </summary>
        /// <param name="bags">The chain.</param>
        /// <param name="variableCapacity">The number of variable slots.</param>
        /// <returns>The count per variable index.</returns>
        static int[] CountVariableUsagePerBag(List<PlanningContext> bags, int variableCapacity)
        {
            var usage = new int[variableCapacity];
            foreach (var bag in bags)
                foreach (var (variable, _) in bag.Variables)
                    usage[variable.Index]++;

            return usage;
        }

    }

}
