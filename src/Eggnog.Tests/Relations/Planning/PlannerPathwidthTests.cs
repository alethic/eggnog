using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The chain the planner builds over a tree decomposition minimizes the pathwidth, where egglog's score does not.
    /// </summary>
    public class PlannerPathwidthTests
    {

        static PlanningContext Bag(Variable[] vars, int[] atoms)
        {
            var vmap = new DenseIdMap<Variable, VarInfo>();
            foreach (var v in vars)
                vmap.Insert(v, new VarInfo());

            var amap = new DenseIdMap<AtomId, QueryAtom>();
            foreach (var atom in atoms)
                amap.Insert(new AtomId(atom), new QueryAtom(new TableId(0), new VarColumnMap(), [], []));

            return new PlanningContext(vmap, amap, new FunDeps(), new PlanTestDatabase(), new long[16], 16);
        }

        [Fact]
        public void ChainContinuationMinimizesPathwidth()
        {
            // Tree: bag3 (root) has children bag1 (one atom, a leaf) and bag2 (one atom) whose child bag0 holds four
            // atoms. Continuing the chain through bag2 absorbs only bag1 into the root: chain [bag0, bag2, bag3+bag1],
            // widest bag 4 atoms. Continuing through bag1 absorbs bag2 and its whole subtree into the root: chain
            // [bag1, bag3+bag2+bag0], widest bag 6 atoms.
            //
            // The doc comment (and egglog's, plan.rs:704-716) says the continuation minimizes the pathwidth. egglog's
            // score unions the root with the subtrees of the *candidate's* children (all_children_list[*chain_child],
            // plan.rs:735-740), a slip of egglog commit d19135d6: its pathwidth commit 7f4cbc49 unions the candidate's
            // *siblings* (all_children_list[i]), which are what the walk absorbs. The planner here scores the siblings;
            // and since it absorbs the siblings all_children skips, egglog's score would pick the widest layout.
            Variable x = new(0), q = new(1), y = new(2), s = new(3), z = new(4);
            var bag0 = Bag([x, q], [0, 1, 2, 3]);
            var bag1 = Bag([y, s], [4]);
            var bag2 = Bag([x, z], [5]);
            var bag3 = Bag([z, y], [6]);

            var chain = QueryPlanner.TopologicallySortBags([bag0, bag1, bag2, bag3]);
            var widths = chain.Select(b => b.Atoms.Count).ToArray();
            Assert.Equal(4, widths.Max());
            Assert.Equal([[0, 1, 2, 3], [5], [4, 6]], chain.Select(b => b.Atoms.Select(kv => kv.Key.Index).ToArray()));
        }

    }

}
