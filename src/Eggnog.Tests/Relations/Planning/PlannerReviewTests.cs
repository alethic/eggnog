using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial review of the planner port: tests that expose defects found in review.
    /// </summary>
    public class PlannerReviewTests
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
            // plan.rs:735-740), a typo introduced by the refactor in egglog commit d19135d6 ("review feedback"); the
            // original pathwidth commit 7f4cbc49 unioned the candidate's *siblings* (all_children_list[i]), which are
            // what the walk actually absorbs. With the all_children fix (map 8.1 row 6) the absorbed siblings are real,
            // so the typo now picks the widest layout.
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

        [Fact]
        public void DecomposedHeadersAreInAtomOrder()
        {
            // PlanTemplate.Headers is documented as "the fast constraints of each atom that has any, in atom order"
            // (PlanTemplate.cs:66). A decomposed plan collects them bag by bag (QueryPlanner.cs:111-127), leaf bag
            // first, so path4 with fast constraints on a0 and a3 lists a3 (block 0's bag {a1,a2,a3}) before a0 (block
            // 1's bag {a0,a1,a2}). Column uniqueness uses whole-table numbers, so the constraints do not change the
            // decomposition.
            var db = new PlanTestDatabase();
            (int N, int M)[] sizes = [(100, 30), (10, 5), (50, 20), (20, 10)];
            var tables = sizes.Select(t => db.AddTable(2, 2, PlanTestDatabase.ExperimentRows(t.N, t.M))).ToArray();
            var qb = new QueryBuilder(db);
            var v = Enumerable.Range(0, 5).Select(i => qb.NewVariable()).ToArray();
            qb.AddAtom(tables[0], [v[0], v[1]], [Constraint.EqConst(new ColumnId(0), new Value(1))]);
            qb.AddAtom(tables[1], [v[1], v[2]]);
            qb.AddAtom(tables[2], [v[2], v[3]]);
            qb.AddAtom(tables[3], [v[3], v[4]], [Constraint.EqConst(new ColumnId(1), new Value(4))]);
            qb.MarkUsed(v[0]);
            qb.MarkUsed(v[4]);
            var plan = Assert.IsType<DecomposedPlan>(QueryPlanner.PlanQuery(qb.Build(new ActionId(0)), db));

            var atoms = plan.Headers.Select(h => h.Atom.Index).ToArray();
            Assert.Equal(atoms.Order(), atoms);
        }

        [Fact]
        public void ColumnUniquenessEqualityAgreesWithItsOrdering()
        {
            // egglog's ColUniqueness implements PartialEq as cmp(other) == Equal (free_join/mod.rs:957-961): 100 rows
            // over 50 values equals 10 rows over 5 values. The port's record struct compares the fields, so Equals and
            // == disagree with CompareTo (and with egglog).
            var halves = new ColumnUniqueness(100, 50);
            var same = new ColumnUniqueness(10, 5);
            Assert.Equal(0, halves.CompareTo(same));
            Assert.True(halves.Equals(same));
            Assert.True(halves == same);
        }

    }

}
