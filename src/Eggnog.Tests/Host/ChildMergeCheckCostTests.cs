using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// What a child's host batch pays for the refusal of the merges that would rewrite a row of a function whose merge is
    /// not a lattice (<see cref="FunctionMerge"/>, decisions row 2): a row of one keyed by a vector of classes costs a
    /// batch whose merges no vector reaches nothing beyond its congruence check, however many facts the batch has and
    /// however many rows the check reads. The check's replay of the rebuild's passes runs below the root for such rows
    /// only once a class that loses is held by a container of a kind they hold, since no container they name changes
    /// before; were it to run whenever such a row names a container, it would read every row of the check at the end of
    /// every fact.
    /// </summary>
    [Collection(Relations.Tests.AllocationProbeCollection.Name)]
    public sealed class ChildMergeCheckCostTests
    {

        static readonly FunctionMerge<long> Sum = FunctionMerge.Of<long>("sum", (a, b) => checked(a + b));

        /// <summary>
        /// The number of <c>Add</c> terms of the root, the rows of the check.
        /// </summary>
        const int Rows = 1000;

        /// <summary>
        /// The number of facts of the child's batch after its union, each a new <c>Add</c> term, a row of the check.
        /// </summary>
        const int Facts = 200;

        /// <summary>
        /// The child's batch allocates within 1.5× as much with the row of such a function as without it, where a replay
        /// at the end of each fact would allocate some fifty times as much. Probe child-unheld-merge-cost.egg, which
        /// egglog at 5e655cae runs clean, is the workload in small: (Add (Num i) (Num i+1)) for some i,
        /// (set (sum (vec-of (Num 0))) 1), and in a push (union (Num 100) (Num 101)) and more Add terms, after which sum
        /// is 1 as before; the library accepts the union too.
        /// </summary>
        [Fact]
        public void ARowOfAFunctionWithAMergeKeyedByAVectorCostsNothingToABatchWhoseMergesNoVectorReaches()
        {
            var without = LeastBytes(false);
            var with = LeastBytes(true);
            Assert.True(with < 1.5 * without, $"the batch allocates {with} bytes with the row and {without} without");
        }

        /// <summary>
        /// Measures the least the child's batch allocates on the current thread over three new graphs, the root built
        /// and frozen first; the graph is serial, so the batch allocates on this thread only. Checks the batch's
        /// verdict each time.
        /// </summary>
        static long LeastBytes(bool keyed)
        {
            var least = long.MaxValue;
            for (var run = 0; run < 3; run++)
            {
                var graph = new EGraph();
                graph.DefineSort<Node>("Math");
                var num = graph.DefineConstructor<long, Node>("Num");
                var add = graph.DefineConstructor<Node, Node, Node>("Add");
                graph.Values.RegisterVec<Node>();
                var sum = graph.DefineFunction<Vec<Node>, long>("sum", Sum);
                var row = sum.Of(Primitives.VecOf<Node>().Of(num.Of(0L)));
                var root = graph.Root;
                root.Write(w =>
                {
                    for (var i = 0; i < Rows; i++)
                        w.Assume(Fact.Exists(add.Of(num.Of((long)i), num.Of((long)i + 1))));
                });
                if (keyed)
                    root.Assume(row.Is(1L));

                var a = root.Make(num.Of(Rows + 10L));
                var b = root.Make(num.Of(Rows + 11L));
                root.Freeze();
                using var child = root.CreateChild();
                var before = GC.GetAllocatedBytesForCurrentThread();
                child.Write(w =>
                {
                    w.Union(a, b);
                    for (var j = 0; j < Facts; j++)
                        w.Assume(Fact.Exists(add.Of(num.Of((long)j), num.Of((long)j + 3))));
                });
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);

                Assert.False(child.IsFaulted);
                Assert.True(child.AreEqual(a, b));
                if (keyed)
                    Assert.Equal(1L, child.Get(row));
            }

            return least;
        }

    }

}
