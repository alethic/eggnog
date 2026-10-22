using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class Extractor
    {

        /// <summary>
        /// A binary min-heap of node indices ordered by cost. Each node enters it at most once, when the last class it
        /// waits on becomes final, with its exact cost, so it never needs a decrease-key.
        /// </summary>
        int[] heap = null!;
        int heapCount;

        /// <summary>
        /// Runs Knuth's algorithm until the queue is empty: pops the cheapest queued node; if its class is not final yet,
        /// makes the class final at the node's cost, and queues every node waiting on the class whose last pending
        /// dependency this was. Computes the costs egglog's <c>bellman_ford</c> converges to (src/extract.rs:397-466);
        /// which node a class extracts as is decided afterwards, in egglog's order (Extractor.Choices.cs).
        /// </summary>
        /// <remarks>
        /// O((nodes queued) log (nodes queued) + occurrences released). Optimal because costs are saturating sums of
        /// costs, which never decrease along a hyperedge, so the costs are the least fixpoint Bellman-Ford
        /// reaches. The order in which equal-cost nodes leave the queue changes no cost, so it is left to the heap.
        /// </remarks>
        void RunKnuth()
        {
            while (heapCount > 0)
            {
                var node = Pop();
                var owner = nodeClass[node];

                // Its class became final first.
                if (classKnown[owner])
                    continue;

                classCost[owner] = nodeCost[node];
                classKnown[owner] = true;
                for (var occurrence = classFirstParent[owner]; occurrence >= 0; occurrence = occNext[occurrence])
                {
                    var parent = occNode[occurrence];
                    if (--nodePending[parent] > 0)
                        continue;

                    if (classKnown[nodeClass[parent]] == false)
                        Push(parent, ComputeNodeCost(parent));
                }

                // The waiting list is spent: every occurrence on it has been released.
                classFirstParent[owner] = -1;
            }
        }

        /// <summary>
        /// Queues a node whose dependencies are all final, at its cost.
        /// </summary>
        void Push(int node, ulong cost)
        {
            nodeCost[node] = cost;
            if (heapCount == heap.Length)
                Array.Resize(ref heap, heapCount * 2);

            // Sift up.
            var position = heapCount++;
            while (position > 0)
            {
                var parent = (position - 1) >> 1;
                if (nodeCost[node] >= nodeCost[heap[parent]])
                    break;

                heap[position] = heap[parent];
                position = parent;
            }

            heap[position] = node;
        }

        /// <summary>
        /// Removes and returns the cheapest node of the queue.
        /// </summary>
        int Pop()
        {
            Debug.Assert(heapCount > 0, "the queue is not empty");

            var first = heap[0];
            var last = heap[--heapCount];
            if (heapCount == 0)
                return first;

            // Sift the last node down from the root.
            var position = 0;
            var cost = nodeCost[last];
            while (true)
            {
                var child = (position << 1) + 1;
                if (child >= heapCount)
                    break;

                if (child + 1 < heapCount && Cheaper(heap[child + 1], heap[child]))
                    child++;

                if (nodeCost[heap[child]] >= cost)
                    break;

                heap[position] = heap[child];
                position = child;
            }

            heap[position] = last;
            return first;
        }

        /// <summary>
        /// Gets whether queued node <paramref name="left"/> costs less than <paramref name="right"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Cheaper(int left, int right) => nodeCost[left] < nodeCost[right];

    }

}
