using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="BucketQueue"/> (egglog's greedy set cover, core-relations/src/free_join/plan.rs:1302-1366)
    /// and <see cref="AtomIndexSet"/>.
    /// </summary>
    public class BucketQueueTests
    {

        static PlanningContext Context(Query query, PlanTestDatabase db) =>
            new(PlanningContext.CloneVariables(query.Variables), query.Atoms.Clone(), query.FunDeps, db, new long[query.Atoms.IdCount], query.Variables.IdCount);

        static List<int> PopAll(BucketQueue queue)
        {
            var popped = new List<int>();
            while (queue.TryPop(out var atom))
                popped.Add(atom.Index);

            return popped;
        }

        [Fact]
        public void TakesTheLastAtomOfTheLargestBucket()
        {
            // Triangle: every atom has two uncovered variables; indexmap's pop takes the last, a2, which covers v0 and
            // v2 and leaves a0 and a1 with one each; then a1 (moved last by swap_remove) covers v1.
            var (db, query) = ExperimentQueries.Build("triangle", PlanStrategy.MinCover);
            Assert.Equal([2, 1], PopAll(new BucketQueue(Context(query, db))));
        }

        [Fact]
        public void VariableFreeAtomDoesNotPanic()
        {
            // egglog unwraps an empty bucket 0 after popping a variable-free atom (map §8.1 row 7).
            var (db, query) = ExperimentQueries.Build("allconst", PlanStrategy.MinCover);
            Assert.Equal([1, 0], PopAll(new BucketQueue(Context(query, db))));
        }

        [Fact]
        public void OnlyVariablesOfTheBagCount()
        {
            // A bag whose atom a1 also holds v2, which is not in the bag: egglog indexed var_info[v2] and panicked.
            var (db, query) = ExperimentQueries.Build("join2_unused", PlanStrategy.MinCover);
            var ctx = Context(query, db);
            ctx.Variables.Take(new Variable(2));

            // a0 has two bag variables, a1 one (v2 is ignored); a0 covers both.
            Assert.Equal([0], PopAll(new BucketQueue(ctx)));
        }

        [Fact]
        public void IndexSetMatchesIndexmapSemantics()
        {
            var set = new AtomIndexSet();
            foreach (var i in new[] { 4, 1, 7, 3 })
                Assert.True(set.Insert(new AtomId(i)));
            Assert.False(set.Insert(new AtomId(1)));

            // swap_remove(4) moves the last element (3) into slot 0: [3, 1, 7].
            Assert.True(set.SwapRemove(new AtomId(4)));
            Assert.False(set.SwapRemove(new AtomId(4)));
            var order = new List<int>();
            while (set.Pop(out var atom))
                order.Add(atom.Index);

            Assert.Equal([7, 1, 3], order);
            Assert.Equal(0, set.Count);
        }

        [Fact]
        public void CoverIsComplete()
        {
            // Every variable of every atom is covered by some popped atom.
            var rng = new System.Random(11);
            for (var i = 0; i < 300; i++)
            {
                var (db, query) = RandomQueries.Generate(rng);
                var ctx = Context(query, db);
                var popped = PopAll(new BucketQueue(ctx)).Select(a => new AtomId(a)).ToList();
                var covered = popped.SelectMany(a => Vars(query.Atoms[a])).ToHashSet();
                foreach (var (_, atom) in query.Atoms)
                    Assert.Subset(covered, Vars(atom).ToHashSet());

                Assert.Equal(popped.Count, popped.Distinct().Count());
            }
        }

        static IEnumerable<Variable> Vars(QueryAtom atom)
        {
            var list = new List<Variable>();
            foreach (var (_, v) in atom.VarColumns)
                list.Add(v);

            return list;
        }

    }

}
