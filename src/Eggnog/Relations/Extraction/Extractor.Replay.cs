using System.Diagnostics;

namespace Eggnog.Relations
{

    internal sealed partial class Extractor
    {

        // Replaying earlier queries' finalizations, on demand.
        //
        // An on-demand extractor runs Knuth's algorithm once per query, over the classes that query discovered. A node
        // discovered now may depend on a class that became final in an earlier query. A from-scratch run over
        // everything discovered so far would release that node only when the class's chosen node leaves the queue, at
        // its (cost, content order) position. Queuing the node at once instead lets it overtake nodes of its own cost
        // that a from-scratch run pops first, and so changes which node wins a tie: the choice would depend on which
        // values were asked about before. That happens only when the class costs as much as the node (zero costs, or
        // costs saturated at long.MaxValue); otherwise the class is final, in a from-scratch run, before any node of
        // the node's cost leaves the queue, and queuing the node early changes nothing (WaitOnChildren).
        //
        // For a class that does cost as much, the node waits on it, and the class's finalization is replayed: its chosen
        // node is queued again at its cost, once the replays of the children it must wait on by the same rule (those of
        // equal cost) are done, and when it leaves the queue it releases the class's waiting nodes instead of finalizing
        // the class again. The replayed events are exactly the earlier run's finalizations of those classes, at the
        // same positions relative to one another (nodes discovered now never affect classes already final, whose nodes
        // depend only on classes discovered earlier), so the run reproduces a from-scratch run over everything
        // discovered: on-demand choices never depend on the order of queries, and agree with the root-sort extractor's.
        // A replay touches only the chosen nodes of equal cost below the class, so with positive head costs and no
        // saturation nothing is replayed at all.

        /// <summary>
        /// The number of the current query's run of Knuth's algorithm; a class whose <c>classReplay</c> holds it has had
        /// its replay requested in this run.
        /// </summary>
        int run;

        /// <summary>
        /// Reused stack of classes whose replay was requested but whose chosen node is not yet armed.
        /// </summary>
        int[] replayStack = new int[16];

        /// <summary>
        /// The number of class finalizations replayed so far, for tests.
        /// </summary>
        internal int ReplayCount { get; private set; }

        /// <summary>
        /// Requests the replay of the finalization of <paramref name="root"/>, a class final since an earlier query, in
        /// the current run, and of the classes of equal cost its chosen node depends on, recursively: each chosen node
        /// waits on the replays of its children that cost as much as it does, and is queued at once if there are none.
        /// Idempotent within a run. O(chosen nodes of equal cost below <paramref name="root"/> and their occurrences),
        /// iteratively, so long chains of zero-cost choices do not overflow the stack.
        /// </summary>
        /// <param name="root">A final class.</param>
        void RequestReplay(int root)
        {
            Debug.Assert(onDemand && classBest[root] >= 0, "only an on-demand extractor replays, and only final classes");
            if (classReplay[root] == run)
                return;

            classReplay[root] = run;
            var count = 0;
            replayStack[count++] = root;
            while (count > 0)
            {
                var replayed = replayStack[--count];
                var chosen = classBest[replayed];
                var cost = classCost[replayed];
                var start = nodeChildStart[chosen];
                var end = start + nodeChildCount[chosen];
                var pending = 0;
                for (var k = start; k < end; k++)
                {
                    var dependency = childClasses[k];
                    Debug.Assert(
                        classBest[dependency] >= 0 && classCost[dependency] <= cost,
                        "a choice's children are final and cost no more");

                    // A cheaper child is final, in a from-scratch run, before any node of this cost leaves the queue.
                    if (classCost[dependency] < cost)
                        continue;

                    Wait(chosen, dependency);
                    pending++;
                    if (classReplay[dependency] != run)
                    {
                        classReplay[dependency] = run;
                        EnsureCapacity(ref replayStack, count + 1);
                        replayStack[count++] = dependency;
                    }
                }

                nodePending[chosen] = pending;
                ReplayCount++;
                if (pending == 0)
                    Push(chosen, cost);
            }
        }

    }

}
