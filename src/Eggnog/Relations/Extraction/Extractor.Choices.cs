using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class Extractor
    {

        // Choosing in egglog's order.
        //
        // egglog's extractor relaxes rows in passes (bellman_ford, src/extract.rs:407-460): each pass visits the functions
        // reachable from its root sorts in breadth-first order (src/extract.rs:246-286), each function's rows in storage
        // order, subsumed rows skipped, and updates a class whenever a row costs less than the class's cost so far, until
        // a pass updates nothing; each update stamps the class with the next topological rank. A class then extracts as
        // the first row, in the same order, that costs exactly the class's cost and whose class's rank exceeds the rank
        // of every class among its children (src/extract.rs:461-499). The rank is that of the class's last update, so the
        // children of the row reached their final costs before the class did, which keeps the choices acyclic when costs
        // tie.
        //
        // Knuth's algorithm has the costs (Extractor.Knuth.cs); the ranks follow from them without running the passes.
        // Call an event's time its (pass, row) pair, ordered by pass, then by the row's function's place in the order,
        // then by the row's position (ExtractionOrder). A class whose cost is below the saturation point reaches that
        // cost at the first relaxation of one of its rows after all the row's children have reached theirs, and only
        // then: a sum of costs at least the children's final costs equals the row's final cost only if every child is
        // at its own, and a sum that saturates is not below the saturation point. Its last update is therefore at the
        // least, over its tight rows (those that cost exactly its cost), of the next relaxation of the row after the
        // latest last update among the row's children: in the same pass if the row comes after that update, else in the
        // next. That is a shortest-path problem over times whose steps only go forward, which Knuth's algorithm solves
        // again, over the tight rows (RunTimes). A class whose cost saturates has that cost from its first update on, so
        // its last update is its first: the least, over all its rows with a cost, of the next relaxation of the row
        // after the latest first update among its children, which the same algorithm finds over those rows
        // (FirstTimes). Only the order of the ranks decides anything, and the times are ordered exactly as the ranks.
        //
        // A class's times depend only on the rows below it, so the times and choices of the classes a query reaches are
        // what egglog's extractor computes over the whole e-graph. Each order computes them once per class, for the
        // classes its queries reach, whatever was asked before. The root-sort extractor has one order, its functions';
        // on demand, each queried sort has its own, as egglog's extract command builds an extractor rooted at the sort
        // of what it extracts (src/lib.rs:1609-1622).

        /// <summary>
        /// The only order of an extractor made by <see cref="ComputeCostsFromRootSorts"/>, once used.
        /// </summary>
        ExtractionOrder? scanOrder;

        /// <summary>
        /// The order of each queried sort of an on-demand extractor, by sort index.
        /// </summary>
        ExtractionOrder?[] sortOrders = [];

        // Scratch for computing times: per node, the latest time among the children so far, the number of children
        // still without a time, and the pass of the node's own relaxation once queued; per class, the nodes waiting on it.
        int[] waitPending = [];
        int[] waitPass = [];
        int[] waitNode = [];
        int[] timePass = [];
        int[] classWaiters = [];
        int[] waiterNode = new int[16];
        int[] waiterNext = new int[16];
        int waiterCount;
        int[] timeHeap = new int[16];
        int timeHeapCount;
        int[] solveStack = new int[16];
        readonly List<int> collected = new();
        readonly List<int> collectedFirst = new();

        /// <summary>
        /// Gets the order a query about a value of the sort at <paramref name="sort"/> is answered in: the extractor's own
        /// at the root sorts, or on demand that of an extractor rooted at the sort. Allocates the order on first use.
        /// </summary>
        /// <param name="sort">The queried sort's index.</param>
        /// <returns>The order.</returns>
        ExtractionOrder OrderFor(int sort)
        {
            if (onDemand == false)
            {
                if (scanOrder is null)
                {
                    var identity = new int[slots.Length];
                    for (var slot = 0; slot < identity.Length; slot++)
                        identity[slot] = slot;

                    scanOrder = new ExtractionOrder(identity);
                }

                return scanOrder;
            }

            if (sort >= sortOrders.Length)
                Array.Resize(ref sortOrders, Math.Max(sort + 1, sortOrders.Length * 2));

            return sortOrders[sort] ??= new ExtractionOrder(BreadthFirst(sort));
        }

        /// <summary>
        /// Ranks the slots as egglog lists the functions of an extractor rooted at the sort at <paramref name="root"/>:
        /// breadth-first over sorts, a container sort leading to its element sorts and an e-class sort to the functions
        /// producing it, in declaration order, whose child sorts are queued in turn (src/extract.rs:246-286).
        /// </summary>
        /// <param name="root">The root sort's index.</param>
        /// <returns>Each slot's place in the list; <c>-1</c> for a slot not reached.</returns>
        int[] BreadthFirst(int root)
        {
            var rank = new int[slots.Length];
            Array.Fill(rank, -1);
            var next = 0;
            var seen = new HashSet<int> { root };
            var queue = new Queue<int>();
            queue.Enqueue(root);
            while (queue.TryDequeue(out var sort))
            {
                if (sortKinds[sort] == SortKind.Container)
                {
                    foreach (var inner in ((ContainerSort)sorts[sort]).InnerSorts)
                    {
                        var index = SortIndexOf(inner);
                        if (seen.Add(index))
                            queue.Enqueue(index);
                    }
                }
                else if (sortKinds[sort] == SortKind.Eq)
                {
                    foreach (var slot in sortProducers[sort])
                    {
                        if (rank[slot] >= 0)
                            continue;

                        rank[slot] = next++;
                        var columnSorts = slotColumnSorts[slot];
                        for (var i = 0; i < slots[slot].ChildCount; i++)
                            if (seen.Add(columnSorts[i]))
                                queue.Enqueue(columnSorts[i]);
                    }
                }
            }

            return rank;
        }

        /// <summary>
        /// Makes sure the classes the term of <paramref name="value"/>, a costed value of the sort at
        /// <paramref name="sort"/>, is built from have their choices in <paramref name="order"/>: the value's class, or
        /// the classes inside a container.
        /// </summary>
        void EnsureChoices(ExtractionOrder order, int sort, uint value)
        {
            switch (sortKinds[sort])
            {
                case SortKind.Eq:
                    EnsureChoices(order, FindClass(sort, value));
                    break;
                case SortKind.Container:
                    var container = containerIndex[Key(sort, value)];
                    var start = containerClassStart[container];
                    var end = start + containerClassCount[container];
                    for (var k = start; k < end; k++)
                        EnsureChoices(order, containerClasses[k]);

                    break;
            }
        }

        /// <summary>
        /// Makes sure <paramref name="root"/>, a class with a cost, and every class its term may be built from have their
        /// times and choices in <paramref name="order"/>: collects the classes below it not yet solved, through their
        /// tight nodes, computes their times (<see cref="RunTimes"/>, with <see cref="FirstTimes"/> for saturated costs)
        /// and then their choices (<see cref="Choose"/>). O(nodes and occurrences of the classes collected, plus heap
        /// operations); nothing for a class already solved.
        /// </summary>
        void EnsureChoices(ExtractionOrder order, int root)
        {
            Debug.Assert(root >= 0 && classCost[root] >= 0, "only a class with a cost has a choice");
            order.Grow(classCount);
            if (order.State[root] != ExtractionOrder.Unsolved)
                return;

            var saturated = Collect(order.State, root, collected, everyRow: false);
            if (saturated)
                FirstTimes(order);

            RunTimes(order, collected, order.State, order.FinalPass, order.FinalNode, everyRow: false);
            foreach (var owner in collected)
            {
                if (classCost[owner] == long.MaxValue)
                {
                    order.FinalPass[owner] = order.FirstPass![owner];
                    order.FinalNode[owner] = order.FirstNode![owner];
                }
            }

            foreach (var owner in collected)
                order.Choice[owner] = Choose(order, owner);

            foreach (var owner in collected)
                order.State[owner] = ExtractionOrder.Solved;
        }

        /// <summary>
        /// Computes the first updates of the saturated classes just collected, and of the classes below them through every
        /// node with a cost, those not yet known in <paramref name="order"/>.
        /// </summary>
        void FirstTimes(ExtractionOrder order)
        {
            order.GrowFirst(classCount);
            var state = order.FirstState!;
            collectedFirst.Clear();
            foreach (var owner in collected)
                if (classCost[owner] == long.MaxValue && state[owner] == ExtractionOrder.Unsolved)
                    Collect(state, owner, collectedFirst, everyRow: true, append: true);

            RunTimes(order, collectedFirst, state, order.FirstPass!, order.FirstNode!, everyRow: true);
            foreach (var owner in collectedFirst)
                state[owner] = ExtractionOrder.Solved;
        }

        /// <summary>
        /// Collects into <paramref name="into"/> <paramref name="root"/> and the classes below it whose state is
        /// unsolved, through each class's tight nodes (or, with <paramref name="everyRow"/>, every node with a cost), and
        /// marks them collecting.
        /// </summary>
        /// <returns>Whether a class collected has a saturated cost.</returns>
        bool Collect(int[] state, int root, List<int> into, bool everyRow, bool append = false)
        {
            if (append == false)
                into.Clear();

            var saturated = false;
            var count = 0;
            state[root] = ExtractionOrder.Collecting;
            into.Add(root);
            solveStack[count++] = root;
            while (count > 0)
            {
                var owner = solveStack[--count];
                saturated |= classCost[owner] == long.MaxValue;
                for (var node = classFirstNode[owner]; node >= 0; node = nodeNext[node])
                {
                    if (everyRow ? IsCostable(node) == false : IsTight(node) == false)
                        continue;

                    var start = nodeChildStart[node];
                    var end = start + nodeChildCount[node];
                    for (var k = start; k < end; k++)
                    {
                        var child = childClasses[k];
                        if (state[child] != ExtractionOrder.Unsolved)
                            continue;

                        state[child] = ExtractionOrder.Collecting;
                        into.Add(child);
                        EnsureCapacity(ref solveStack, count + 1);
                        solveStack[count++] = child;
                    }
                }
            }

            return saturated;
        }

        /// <summary>
        /// Gets whether a node costs exactly its class's cost, once its children are final: egglog's candidate parent
        /// edges (src/extract.rs:468-472).
        /// </summary>
        bool IsTight(int node) => IsCostable(node) && ComputeNodeCost(node) == classCost[nodeClass[node]];

        /// <summary>
        /// Computes the times of the classes in <paramref name="classes"/>, those collecting in <paramref name="state"/>,
        /// with Knuth's algorithm over times: a node is queued, at the next relaxation of its row after the latest time
        /// among its children, once every child has a time, and the first node of a class to leave the queue gives the
        /// class its time. The last updates run over the tight nodes of the classes whose costs do not saturate; the
        /// first updates, with <paramref name="everyRow"/>, over every node with a cost.
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="classes">The classes to time.</param>
        /// <param name="state">The classes' states; those collecting are timed here, those solved have their times.</param>
        /// <param name="passes">Receives each class's pass.</param>
        /// <param name="nodes">Receives each class's node.</param>
        /// <param name="everyRow">Whether to time first updates rather than last ones.</param>
        void RunTimes(ExtractionOrder order, List<int> classes, int[] state, int[] passes, int[] nodes, bool everyRow)
        {
            EnsureCapacity(ref waitPending, nodeCount);
            EnsureCapacity(ref waitPass, nodeCount);
            EnsureCapacity(ref waitNode, nodeCount);
            EnsureCapacity(ref timePass, nodeCount);
            EnsureCapacity(ref classWaiters, classCount);
            waiterCount = 0;
            timeHeapCount = 0;
            foreach (var owner in classes)
            {
                classWaiters[owner] = -1;
                passes[owner] = -1;
            }

            foreach (var owner in classes)
            {
                // A saturated class takes its first update as its last.
                if (everyRow == false && classCost[owner] == long.MaxValue)
                    continue;

                for (var node = classFirstNode[owner]; node >= 0; node = nodeNext[node])
                {
                    if (everyRow ? IsCostable(node) == false : IsTight(node) == false)
                        continue;

                    var pending = 0;
                    var latestPass = -1;
                    var latestNode = -1;
                    var start = nodeChildStart[node];
                    var end = start + nodeChildCount[node];
                    for (var k = start; k < end; k++)
                    {
                        var child = childClasses[k];
                        if (state[child] == ExtractionOrder.Collecting)
                        {
                            AddWaiter(child, node);
                            pending++;
                        }
                        else
                        {
                            Debug.Assert(state[child] == ExtractionOrder.Solved, "a child is timed here or before");
                            if (IsLater(order, passes[child], nodes[child], latestPass, latestNode))
                            {
                                latestPass = passes[child];
                                latestNode = nodes[child];
                            }
                        }
                    }

                    waitPending[node] = pending;
                    waitPass[node] = latestPass;
                    waitNode[node] = latestNode;
                    if (pending == 0)
                        PushTime(order, node, NextPass(order, node, latestPass, latestNode));
                }
            }

            while (timeHeapCount > 0)
            {
                var node = PopTime(order);
                var owner = nodeClass[node];

                // Its class has its time already.
                if (passes[owner] >= 0)
                    continue;

                var pass = timePass[node];
                passes[owner] = pass;
                nodes[owner] = node;
                for (var waiter = classWaiters[owner]; waiter >= 0; waiter = waiterNext[waiter])
                {
                    var parent = waiterNode[waiter];
                    if (IsLater(order, pass, node, waitPass[parent], waitNode[parent]))
                    {
                        waitPass[parent] = pass;
                        waitNode[parent] = node;
                    }

                    if (--waitPending[parent] == 0 && passes[nodeClass[parent]] < 0)
                        PushTime(order, parent, NextPass(order, parent, waitPass[parent], waitNode[parent]));
                }
            }

#if DEBUG
            foreach (var owner in classes)
                Debug.Assert(
                    passes[owner] >= 0 || (everyRow == false && classCost[owner] == long.MaxValue),
                    "every class with a cost has a time");
#endif
        }

        /// <summary>
        /// Links <paramref name="node"/> into the list of nodes waiting for <paramref name="dependency"/>'s time.
        /// </summary>
        void AddWaiter(int dependency, int node)
        {
            if (waiterCount == waiterNode.Length)
            {
                Array.Resize(ref waiterNode, waiterCount * 2);
                Array.Resize(ref waiterNext, waiterCount * 2);
            }

            waiterNode[waiterCount] = node;
            waiterNext[waiterCount] = classWaiters[dependency];
            classWaiters[dependency] = waiterCount++;
        }

        /// <summary>
        /// Gets the pass of the first relaxation of <paramref name="node"/>'s row after the event at
        /// (<paramref name="pass"/>, <paramref name="after"/>): the same pass when the row comes later in it, else the
        /// next; the first pass after no event.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int NextPass(ExtractionOrder order, int node, int pass, int after)
        {
            if (pass < 0)
                return 0;

            return Compare(order, node, after) > 0 ? pass : pass + 1;
        }

        /// <summary>
        /// Gets whether the event at (<paramref name="leftPass"/>, <paramref name="leftNode"/>) comes after the one at
        /// (<paramref name="rightPass"/>, <paramref name="rightNode"/>); pass <c>-1</c> is no event, before every other.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsLater(ExtractionOrder order, int leftPass, int leftNode, int rightPass, int rightNode)
        {
            if (leftPass != rightPass)
                return leftPass > rightPass;

            return leftPass >= 0 && Compare(order, leftNode, rightNode) > 0;
        }

        /// <summary>
        /// Orders two nodes as one pass meets them: by their functions' places in the order, then by where their rows are
        /// stored. Distinct nodes never compare equal.
        /// </summary>
        /// <returns>The sign of the comparison.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int Compare(ExtractionOrder order, int left, int right)
        {
            var leftRank = order.SlotRank[nodeSlot[left]];
            var rightRank = order.SlotRank[nodeSlot[right]];
            Debug.Assert(leftRank >= 0 && rightRank >= 0, "the nodes below a queried class are in its order");
            if (leftRank != rightRank)
                return leftRank.CompareTo(rightRank);

            return nodePosition[left].CompareTo(nodePosition[right]);
        }

        /// <summary>
        /// Queues a node for the time-ordered run at its relaxation in <paramref name="pass"/>.
        /// </summary>
        void PushTime(ExtractionOrder order, int node, int pass)
        {
            timePass[node] = pass;
            if (timeHeapCount == timeHeap.Length)
                Array.Resize(ref timeHeap, timeHeapCount * 2);

            var position = timeHeapCount++;
            while (position > 0)
            {
                var parent = (position - 1) >> 1;
                if (IsSooner(order, node, timeHeap[parent]) == false)
                    break;

                timeHeap[position] = timeHeap[parent];
                position = parent;
            }

            timeHeap[position] = node;
        }

        /// <summary>
        /// Removes and returns the node of the soonest relaxation queued.
        /// </summary>
        int PopTime(ExtractionOrder order)
        {
            Debug.Assert(timeHeapCount > 0, "the queue is not empty");

            var first = timeHeap[0];
            var last = timeHeap[--timeHeapCount];
            if (timeHeapCount == 0)
                return first;

            var position = 0;
            while (true)
            {
                var child = (position << 1) + 1;
                if (child >= timeHeapCount)
                    break;

                if (child + 1 < timeHeapCount && IsSooner(order, timeHeap[child + 1], timeHeap[child]))
                    child++;

                if (IsSooner(order, timeHeap[child], last) == false)
                    break;

                timeHeap[position] = timeHeap[child];
                position = child;
            }

            timeHeap[position] = last;
            return first;
        }

        /// <summary>
        /// Gets whether queued node <paramref name="left"/>'s relaxation comes before <paramref name="right"/>'s.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsSooner(ExtractionOrder order, int left, int right)
        {
            if (timePass[left] != timePass[right])
                return timePass[left] < timePass[right];

            return Compare(order, left, right) < 0;
        }

        /// <summary>
        /// Chooses the node <paramref name="owner"/> extracts as: the first of its tight nodes, in the order, whose
        /// class's last update comes after the last update of every class among its children. egglog's parent edge
        /// (src/extract.rs:461-499).
        /// </summary>
        /// <returns>The node, or <c>-1</c> when none qualifies, which only a saturated cost allows.</returns>
        int Choose(ExtractionOrder order, int owner)
        {
            var best = -1;
            var ownerPass = order.FinalPass[owner];
            var ownerNode = order.FinalNode[owner];
            for (var node = classFirstNode[owner]; node >= 0; node = nodeNext[node])
            {
                if (IsTight(node) == false || (best >= 0 && Compare(order, node, best) > 0))
                    continue;

                var latestPass = -1;
                var latestNode = -1;
                var start = nodeChildStart[node];
                var end = start + nodeChildCount[node];
                for (var k = start; k < end; k++)
                {
                    var child = childClasses[k];
                    if (IsLater(order, order.FinalPass[child], order.FinalNode[child], latestPass, latestNode))
                    {
                        latestPass = order.FinalPass[child];
                        latestNode = order.FinalNode[child];
                    }
                }

                if (IsLater(order, ownerPass, ownerNode, latestPass, latestNode))
                    best = node;
            }

            Debug.Assert(best >= 0 || classCost[owner] == long.MaxValue, "a cost below saturation has a choice");
            return best;
        }

    }

}
