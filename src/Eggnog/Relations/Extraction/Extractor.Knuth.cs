using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class Extractor
    {

        /// <summary>
        /// A binary min-heap of node indices ordered by <see cref="Precedes"/>. Each node enters it at most once per run,
        /// when the last class it waits on becomes final, with its exact cost, so it never needs a decrease-key; a chosen
        /// node enters again in a later run only to replay its class's finalization (Extractor.Replay.cs).
        /// </summary>
        int[] heap = null!;
        int heapCount;

        /// <summary>
        /// Runs Knuth's algorithm until the queue is empty: pops the first queued node; if its class is not final yet,
        /// makes the node the class's choice and the class final at the node's cost, and queues every node waiting on the
        /// class whose last pending dependency this was. Replaces egglog's <c>bellman_ford</c> and its parent-edge pass
        /// (src/extract.rs:397-500). On demand, a popped node that is already its class's choice is the replay of a
        /// finalization from an earlier query (Extractor.Replay.cs): it releases the nodes waiting on the class without
        /// finalizing it again.
        /// </summary>
        /// <remarks>
        /// O((nodes queued) log (nodes queued) + occurrences released). Optimal because costs are saturating sums of
        /// non-negative costs, which never decrease along a hyperedge. Acyclic because a node is queued only after all its
        /// dependencies are final, so the finalization order (<c>classRank</c>) is a topological order of the choices.
        /// </remarks>
        void RunKnuth()
        {
            while (heapCount > 0)
            {
                var node = Pop();
                var owner = nodeClass[node];
                if (classBest[owner] < 0)
                {
                    classBest[owner] = node;
                    classCost[owner] = nodeCost[node];
                    classRank[owner] = nextRank++;
                }
                else if (classBest[owner] != node)
                {
                    // Its class became final first.
                    continue;
                }
                else
                {
                    Debug.Assert(onDemand && classReplay[owner] == run, "a choice is queued again only to replay it");
                }

                for (var occurrence = classFirstParent[owner]; occurrence >= 0; occurrence = occNext[occurrence])
                {
                    var parent = occNode[occurrence];
                    if (--nodePending[parent] > 0)
                        continue;

                    var parentClass = nodeClass[parent];
                    if (classBest[parentClass] < 0)
                        Push(parent, ComputeNodeCost(parent));
                    else if (classBest[parentClass] == parent)
                        Push(parent, nodeCost[parent]);
                }

                // The waiting list is spent: every occurrence on it has been released.
                classFirstParent[owner] = -1;
            }
        }

        /// <summary>
        /// Queues a node whose dependencies are all final, at its cost.
        /// </summary>
        void Push(int node, long cost)
        {
            nodeCost[node] = cost;
            if (heapCount == heap.Length)
                Array.Resize(ref heap, heapCount * 2);

            // Sift up.
            var position = heapCount++;
            while (position > 0)
            {
                var parent = (position - 1) >> 1;
                if (Precedes(node, heap[parent]) == false)
                    break;

                heap[position] = heap[parent];
                position = parent;
            }

            heap[position] = node;
        }

        /// <summary>
        /// Removes and returns the first node of the queue.
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
            while (true)
            {
                var child = (position << 1) + 1;
                if (child >= heapCount)
                    break;

                if (child + 1 < heapCount && Precedes(heap[child + 1], heap[child]))
                    child++;

                if (Precedes(heap[child], last) == false)
                    break;

                heap[position] = heap[child];
                position = child;
            }

            heap[position] = last;
            return first;
        }

        /// <summary>
        /// Gets whether queued node <paramref name="left"/> comes before <paramref name="right"/>: by cost, then by
        /// <see cref="CompareNodes"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Precedes(int left, int right)
        {
            var leftCost = nodeCost[left];
            var rightCost = nodeCost[right];
            if (leftCost != rightCost)
                return leftCost < rightCost;

            return CompareNodes(left, right) < 0;
        }

        /// <summary>
        /// Orders two nodes by content, rule-layer decision D10's tie-break after cost: function id (declaration order),
        /// then the row's columns left to right, e-class and container columns by id and base value columns by content
        /// order (<see cref="Sort.CompareValues"/>). Total on distinct nodes: two rows of one function differ in a key.
        /// </summary>
        /// <returns>The sign of the comparison; 0 only for the same node.</returns>
        int CompareNodes(int left, int right)
        {
            if (left == right)
                return 0;

            var leftSlot = nodeSlot[left];
            var rightSlot = nodeSlot[right];
            if (leftSlot != rightSlot)
                return slots[leftSlot].Id.Rep.CompareTo(slots[rightSlot].Id.Rep);

            var columnSorts = slotColumnSorts[leftSlot];
            var leftRow = nodeRow[left];
            var rightRow = nodeRow[right];
            for (var column = 0; column < columnSorts.Length; column++)
            {
                var leftValue = rowCells[leftRow + column];
                var rightValue = rowCells[rightRow + column];
                if (leftValue == rightValue)
                    continue;

                var sort = columnSorts[column];
                if (sortKinds[sort] == SortKind.Base)
                    return sorts[sort].CompareValues(egraph.BaseValues, new Value(leftValue), new Value(rightValue));

                return leftValue.CompareTo(rightValue);
            }

            Debug.Fail("two distinct nodes hold the same row");
            return left.CompareTo(right);
        }

    }

}
