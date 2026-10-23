using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal sealed partial class Extractor
    {

        // The extraction graph, in flat arrays that only grow.
        //
        // A class is an (eq sort, canonical id) pair, with its cheapest cost once Knuth's algorithm has made it final
        // (unknown until then, classKnown). A node is a row of a function in an extractor slot; it belongs to the class
        // in its output column and depends on its child occurrences: each e-class among its child columns, and each
        // e-class among the elements of its container children (recursively), with multiplicity. Base values and
        // containers' own costs, and its head's cost, fold into the node's constant cost. Its position is where the row is stored: its row id,
        // above the index of its layer in a child context's view, or its place in a scan. A class's parent occurrences,
        // linked through occNext, are the (node, occurrence) pairs that wait for it to become final.

        int classCount;
        int[] classSort = null!;
        uint[] classValue = null!;
        int[] classFirstNode = null!;
        int[] classLastNode = null!;
        int[] classFirstParent = null!;
        ulong[] classCost = null!;
        bool[] classKnown = null!;
        Dictionary<ulong, int> classIndex = null!;
        Queue<int> undiscovered = null!;

        int nodeCount;
        int[] nodeSlot = null!;
        int[] nodeClass = null!;
        int[] nodeRow = null!;
        long[] nodePosition = null!;
        ulong[] nodeConst = null!;
        ulong[] nodeCost = null!;
        int[] nodeChildStart = null!;
        int[] nodeChildCount = null!;
        int[] nodePending = null!;
        int[] nodeNext = null!;

        uint[] rowCells = null!;
        int rowLength;
        int[] childClasses = null!;
        int childLength;

        int occCount;
        int[] occNode = null!;
        int[] occNext = null!;

        // Containers, by (sort, id): their constant cost (own cost plus base values inside, recursively), the e-classes
        // inside them (recursively, with multiplicity), and their elements (sort index and value) for reconstruction.

        int containerCount;
        ulong[] containerConst = null!;
        int[] containerClassStart = null!;
        int[] containerClassCount = null!;
        int[] containerElemStart = null!;
        int[] containerElemCount = null!;
        int[] containerClasses = null!;
        int containerClassLength;
        int[] elemSort = null!;
        uint[] elemValue = null!;
        int elemLength;
        Dictionary<ulong, int> containerIndex = null!;
        List<SortedValue> elementScratch = null!;

        /// <summary>
        /// Allocates the empty graph.
        /// </summary>
        void InitializeGraph()
        {
            const int Initial = 16;
            classSort = new int[Initial];
            classValue = new uint[Initial];
            classFirstNode = new int[Initial];
            classLastNode = new int[Initial];
            classFirstParent = new int[Initial];
            classCost = new ulong[Initial];
            classKnown = new bool[Initial];
            classIndex = new Dictionary<ulong, int>();
            undiscovered = new Queue<int>();

            nodeSlot = new int[Initial];
            nodeClass = new int[Initial];
            nodeRow = new int[Initial];
            nodePosition = new long[Initial];
            nodeConst = new ulong[Initial];
            nodeCost = new ulong[Initial];
            nodeChildStart = new int[Initial];
            nodeChildCount = new int[Initial];
            nodePending = new int[Initial];
            nodeNext = new int[Initial];
            rowCells = new uint[Initial * 4];
            childClasses = new int[Initial * 2];
            occNode = new int[Initial * 2];
            occNext = new int[Initial * 2];

            containerConst = new ulong[Initial];
            containerClassStart = new int[Initial];
            containerClassCount = new int[Initial];
            containerElemStart = new int[Initial];
            containerElemCount = new int[Initial];
            containerClasses = new int[Initial];
            elemSort = new int[Initial];
            elemValue = new uint[Initial];
            containerIndex = new Dictionary<ulong, int>();
            elementScratch = new List<SortedValue>();

            heap = new int[Initial];
        }

        /// <summary>
        /// Packs a (sort index, value) pair into a dictionary key.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Key(int sort, uint value) => ((ulong)(uint)sort << 32) | value;

        /// <summary>
        /// Finds the class of a value of an e-class sort.
        /// </summary>
        /// <returns>The class, or -1 if the extractor has not seen it.</returns>
        int FindClass(int sort, uint value) => classIndex.TryGetValue(Key(sort, value), out var found) ? found : -1;

        /// <summary>
        /// Finds or adds the class of a value of an e-class sort. When discovering on demand, a new class is queued for
        /// discovery. Amortized O(1).
        /// </summary>
        int GetOrAddClass(int sort, uint value)
        {
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(classIndex, Key(sort, value), out var exists);
            if (exists)
                return slot;

            var added = classCount;
            if (added == classSort.Length)
            {
                var size = added * 2;
                Array.Resize(ref classSort, size);
                Array.Resize(ref classValue, size);
                Array.Resize(ref classFirstNode, size);
                Array.Resize(ref classLastNode, size);
                Array.Resize(ref classFirstParent, size);
                Array.Resize(ref classCost, size);
                Array.Resize(ref classKnown, size);
            }

            classSort[added] = sort;
            classValue[added] = value;
            classFirstNode[added] = -1;
            classLastNode[added] = -1;
            classFirstParent[added] = -1;
            classCost[added] = 0;
            classKnown[added] = false;
            classCount = added + 1;
            slot = added;
            if (onDemand)
                undiscovered.Enqueue(added);

            return added;
        }

        /// <summary>
        /// Adds a row of the function in <paramref name="slot"/> as a node of the class in its output column, folds its
        /// head, base value and container costs into a constant, records its child occurrences, waits on the classes it
        /// depends on that are not final yet, and queues it if it waits on none. Called once per row read, by the scan
        /// visitors, so a node cost function runs once per row (egglog's runs at every relaxation of the row).
        /// </summary>
        /// <param name="slot">The function's slot.</param>
        /// <param name="position">Where the row is stored, which orders the rows of one function as egglog's scans meet
        /// them.</param>
        /// <param name="cells">The row's schema columns, keys then output; copied.</param>
        internal void AddNode(int slot, long position, ReadOnlySpan<uint> cells)
        {
            var function = slots[slot];
            var columnSorts = slotColumnSorts[slot];
            Debug.Assert(cells.Length == columnSorts.Length, "a row has one cell per schema column");

            var owner = GetOrAddClass(columnSorts[function.OutputIndex], cells[function.OutputIndex]);
            var node = nodeCount;
            if (node == nodeSlot.Length)
                GrowNodes();

            nodeCount = node + 1;
            EnsureCapacity(ref rowCells, rowLength + cells.Length);
            cells.CopyTo(rowCells.AsSpan(rowLength));
            nodeRow[node] = rowLength;
            rowLength += cells.Length;
            nodePosition[node] = position;
            nodeSlot[node] = slot;
            nodeClass[node] = owner;
            nodeNext[node] = -1;
            if (classLastNode[owner] < 0)
                classFirstNode[owner] = node;
            else
                nodeNext[classLastNode[owner]] = node;

            classLastNode[owner] = node;

            var head = slotHeadCostFunction[slot];
            var cost = slotHeadCost[slot];
            if (head is not null)
            {
                var row = new FunctionRow(MemoryMarshal.Cast<uint, Value>(cells), false);
                cost = head(function.Id, row);
            }

            nodeChildStart[node] = childLength;
            for (var i = 0; i < function.ChildCount; i++)
            {
                var sort = columnSorts[i];
                var value = cells[i];
                switch (sortKinds[sort])
                {
                    case SortKind.Eq:
                        AddChild(GetOrAddClass(sort, value));
                        break;
                    case SortKind.Base:
                        cost = CostMath.Add(cost, BaseCost(sort, value));
                        break;
                    default:
                        var container = GetContainer(sort, value);
                        cost = CostMath.Add(cost, containerConst[container]);
                        var start = containerClassStart[container];
                        var end = start + containerClassCount[container];
                        for (var k = start; k < end; k++)
                            AddChild(containerClasses[k]);

                        break;
                }
            }

            nodeChildCount[node] = childLength - nodeChildStart[node];
            nodeConst[node] = cost;
            var pending = WaitOnChildren(node);
            nodePending[node] = pending;

            // Waiting on nothing: a leaf, or (on demand) a node whose children all became final in earlier queries. Its
            // cost is known now.
            if (pending == 0 && classKnown[owner] == false)
                Push(node, ComputeNodeCost(node));
        }

        /// <summary>
        /// Records that the node being added depends on <paramref name="dependency"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AddChild(int dependency)
        {
            EnsureCapacity(ref childClasses, childLength + 1);
            childClasses[childLength++] = dependency;
        }

        /// <summary>
        /// Links <paramref name="node"/> into the waiting lists of the classes it depends on that are not final, and
        /// counts them. A class already final, which on demand means final since an earlier query, has its cost already.
        /// O(occurrences of the node).
        /// </summary>
        /// <param name="node">The node, whose children are recorded.</param>
        /// <returns>The number of occurrences the node waits on.</returns>
        int WaitOnChildren(int node)
        {
            var start = nodeChildStart[node];
            var end = start + nodeChildCount[node];
            var pending = 0;
            for (var k = start; k < end; k++)
            {
                var dependency = childClasses[k];
                if (classKnown[dependency])
                {
                    Debug.Assert(onDemand, "no class is final while scanning for the root sorts");
                    continue;
                }

                Wait(node, dependency);
                pending++;
            }

            return pending;
        }

        /// <summary>
        /// Links an occurrence of <paramref name="dependency"/> in <paramref name="node"/> into the class's waiting list,
        /// which the class's finalization releases.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Wait(int node, int dependency)
        {
            var occurrence = occCount;
            if (occurrence == occNode.Length)
            {
                Array.Resize(ref occNode, occurrence * 2);
                Array.Resize(ref occNext, occurrence * 2);
            }

            occNode[occurrence] = node;
            occNext[occurrence] = classFirstParent[dependency];
            classFirstParent[dependency] = occurrence;
            occCount = occurrence + 1;
        }

        /// <summary>
        /// Computes a node's cost from its constant and its children's final costs. Every class it depends on must be
        /// final. O(occurrences of the node).
        /// </summary>
        ulong ComputeNodeCost(int node)
        {
            var cost = nodeConst[node];
            var start = nodeChildStart[node];
            var end = start + nodeChildCount[node];
            for (var k = start; k < end; k++)
            {
                Debug.Assert(classKnown[childClasses[k]], "a node is costed once its children are final");
                cost = CostMath.Add(cost, classCost[childClasses[k]]);
            }

            return cost;
        }

        /// <summary>
        /// Gets whether every class a node depends on is final, so that it has a cost.
        /// </summary>
        bool IsCostable(int node)
        {
            var start = nodeChildStart[node];
            var end = start + nodeChildCount[node];
            for (var k = start; k < end; k++)
                if (classKnown[childClasses[k]] == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Finds or reads the container <paramref name="value"/> of the container sort at <paramref name="sort"/>: lists
        /// its elements, folds its own cost and its base values' costs into a constant, and collects the e-classes inside
        /// it, recursing into nested containers. egglog's <c>compute_cost_node</c> container case
        /// (src/extract.rs:322-332), memoized per container.
        /// </summary>
        /// <returns>The container's index.</returns>
        /// <exception cref="InvalidOperationException">The container is not in the e-graph, or it contains
        /// itself.</exception>
        internal int GetContainer(int sort, uint value)
        {
            var key = Key(sort, value);
            if (containerIndex.TryGetValue(key, out var found))
            {
                if (found < 0)
                    throw new InvalidOperationException($"Container {value} of sort {sorts[sort].Name} contains itself.");

                return found;
            }

            // Mark it in progress, so a cycle through nested containers is reported rather than recursing forever.
            containerIndex.Add(key, -1);

            var containerSort = (ContainerSort)sorts[sort];
            elementScratch.Clear();
            containerSort.AppendInnerValues(containers, new Value(value), elementScratch);

            // Copy the elements out of the scratch list before recursing, which reuses it.
            var elemStart = elemLength;
            var count = elementScratch.Count;
            EnsureCapacity(ref elemSort, elemLength + count);
            EnsureCapacity(ref elemValue, elemLength + count);
            for (var i = 0; i < count; i++)
            {
                elemSort[elemLength] = SortIndexOf(elementScratch[i].Sort);
                elemValue[elemLength] = elementScratch[i].Value.Rep;
                elemLength++;
            }

            var function = sortContainerCostFunction[sort];
            var cost = sortCost[sort];
            if (function is not null)
                cost = function(containerSort, new Value(value));

            // First pass: costs, resolving nested containers so that the second pass can append this container's classes
            // contiguously.
            for (var i = elemStart; i < elemStart + count; i++)
            {
                switch (sortKinds[elemSort[i]])
                {
                    case SortKind.Base:
                        cost = CostMath.Add(cost, BaseCost(elemSort[i], elemValue[i]));
                        break;
                    case SortKind.Container:
                        cost = CostMath.Add(cost, containerConst[GetContainer(elemSort[i], elemValue[i])]);
                        break;
                }
            }

            // Second pass: the e-classes inside, nested ones included, with multiplicity.
            var classStart = containerClassLength;
            for (var i = elemStart; i < elemStart + count; i++)
            {
                switch (sortKinds[elemSort[i]])
                {
                    case SortKind.Eq:
                        var dependency = GetOrAddClass(elemSort[i], elemValue[i]);
                        EnsureCapacity(ref containerClasses, containerClassLength + 1);
                        containerClasses[containerClassLength++] = dependency;
                        break;
                    case SortKind.Container:
                        var nested = containerIndex[Key(elemSort[i], elemValue[i])];
                        var nestedStart = containerClassStart[nested];
                        var nestedCount = containerClassCount[nested];
                        EnsureCapacity(ref containerClasses, containerClassLength + nestedCount);
                        Array.Copy(containerClasses, nestedStart, containerClasses, containerClassLength, nestedCount);
                        containerClassLength += nestedCount;
                        break;
                }
            }

            var index = containerCount;
            if (index == containerConst.Length)
            {
                var size = index * 2;
                Array.Resize(ref containerConst, size);
                Array.Resize(ref containerClassStart, size);
                Array.Resize(ref containerClassCount, size);
                Array.Resize(ref containerElemStart, size);
                Array.Resize(ref containerElemCount, size);
            }

            containerConst[index] = cost;
            containerClassStart[index] = classStart;
            containerClassCount[index] = containerClassLength - classStart;
            containerElemStart[index] = elemStart;
            containerElemCount[index] = count;
            containerCount = index + 1;
            containerIndex[key] = index;
            return index;
        }

        /// <summary>
        /// Gets a container's cost: its constant plus the costs of the classes inside it, if they are all final.
        /// </summary>
        /// <returns>Whether every class inside is final.</returns>
        internal bool TryGetContainerCost(int container, out ulong cost)
        {
            cost = containerConst[container];
            var start = containerClassStart[container];
            var end = start + containerClassCount[container];
            for (var k = start; k < end; k++)
            {
                var dependency = containerClasses[k];
                if (classKnown[dependency] == false)
                {
                    cost = 0;
                    return false;
                }

                cost = CostMath.Add(cost, classCost[dependency]);
            }

            return true;
        }

        /// <summary>
        /// Gets the name the term of a node carries.
        /// </summary>
        internal string TermNameOf(int node) => slots[nodeSlot[node]].TermName;

        /// <summary>
        /// Gets the function a node is a row of.
        /// </summary>
        internal FunctionId FunctionOf(int node) => slots[nodeSlot[node]].Id;

        void GrowNodes()
        {
            var size = nodeSlot.Length * 2;
            Array.Resize(ref nodeSlot, size);
            Array.Resize(ref nodeClass, size);
            Array.Resize(ref nodeRow, size);
            Array.Resize(ref nodePosition, size);
            Array.Resize(ref nodeConst, size);
            Array.Resize(ref nodeCost, size);
            Array.Resize(ref nodeChildStart, size);
            Array.Resize(ref nodeChildCount, size);
            Array.Resize(ref nodePending, size);
            Array.Resize(ref nodeNext, size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void EnsureCapacity<T>(ref T[] array, int needed)
        {
            if (needed > array.Length)
                Array.Resize(ref array, Math.Max(needed, array.Length * 2));
        }

    }

}
