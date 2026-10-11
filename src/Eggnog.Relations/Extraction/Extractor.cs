using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog.Relations
{

    /// <summary>
    /// Finds the cheapest term of e-classes of an <see cref="EGraph"/>: the port of egglog's <c>Extractor</c>
    /// (src/extract.rs:126-757), computing costs with Knuth's algorithm and breaking ties by content (rule-layer decision
    /// D10) instead of egglog's Bellman-Ford fixpoint and scan-order ties.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The e-graph is a hypergraph: each e-class is a vertex, and each row of a constructor (an e-node) is a hyperedge
    /// from the classes of its children to its own class. A node costs its head's cost plus its children's costs
    /// (<see cref="CostModel"/>), where a base value child costs its own cost and a container child its own cost plus its
    /// elements', so a node depends on every e-class occurring among its children, inside containers included.
    /// </para>
    /// <para>
    /// Knuth's generalization of Dijkstra's algorithm (Knuth 1977) computes every class's cheapest cost in
    /// O(occurrences + nodes log nodes): a node enters a priority queue once all the classes it depends on are final, with
    /// its exact cost, and the first node of a class to leave the queue makes the class final. Sums of non-negative costs
    /// are superior functions, for which this is optimal. Because a node is queued only after its children are final, the
    /// chosen nodes form a DAG even when costs are zero: a class's choice never depends on the class itself, so the
    /// extracted term is always finite (egglog needs a separate topological rank for this, src/extract.rs:397-500).
    /// </para>
    /// <para>
    /// Ties are broken by content (D10): the queue orders nodes by cost, then function id (declaration order), then
    /// their columns left to right, e-class and container columns by id and base value columns by content
    /// (<see cref="BaseSort{TCodec, T}"/>, culture-invariant). The order is total, so the result depends only on the
    /// e-graph's contents and ids, never on scan order, discovery order or the order of queries. With positive head
    /// costs and no saturation a class's choice is the least node, in this order, of those of the cheapest cost; with
    /// zero or saturated costs it is still deterministic.
    /// </para>
    /// <para>
    /// Two ways to create one. <see cref="ComputeCostsFromRootSorts"/> is egglog's: it reads every row of every
    /// constructor reachable from the root sorts and costs every class at once. <see cref="CreateOnDemand"/> reads only
    /// the classes reachable from the values it is asked about (D10: Knuth's algorithm over the classes reachable from
    /// the root), finding each class's nodes through the cached index on its constructors' output columns, and extends
    /// its graph as later queries reach further; classes already costed stay final, because each discovery is closed
    /// downward, and the finalizations of earlier queries that decide ties among newly discovered nodes are replayed in
    /// order (Extractor.Replay.cs), so each query's run is a from-scratch run's over everything discovered. Both give
    /// the same choice for every class they cost, whatever was asked before.
    /// </para>
    /// <para>
    /// The extractor reads the e-graph as it was when created, which must be rebuilt (canonical), as it is after every
    /// flush and run. Using it after the e-graph changed throws. Not thread-safe: queries extend its caches.
    /// </para>
    /// </remarks>
    public sealed partial class Extractor
    {

        readonly ExtractionSchema schema;
        readonly EGraph egraph;
        readonly CostModel costs;
        readonly ExtractionOptions options;
        readonly bool onDemand;
        readonly Sort[] rootSorts;

        readonly ExtractionFunction[] slots;
        readonly int[][] slotColumnSorts;
        readonly long[] slotHeadCost;
        readonly NodeCostFunction?[] slotHeadCostFunction;

        readonly uint createdAt;
        readonly TableVersion[] slotVersions;

        readonly List<Sort> sorts;
        readonly Dictionary<Sort, int> sortIndex;
        readonly Dictionary<string, Sort> sortNames;
        readonly List<SortKind> sortKinds;
        readonly List<long> sortCost;
        readonly List<BaseValueCostFunction?> sortBaseCostFunction;
        readonly List<ContainerCostFunction?> sortContainerCostFunction;
        readonly List<List<int>> sortProducers;

        /// <summary>
        /// Whether a query is running; still set when one threw, which leaves the caches unusable.
        /// </summary>
        bool busy;

        /// <summary>
        /// Initializes a new instance over the functions <paramref name="functions"/> and scans or prepares to discover.
        /// </summary>
        Extractor(
            ExtractionSchema schema,
            CostModel costs,
            ExtractionOptions options,
            bool onDemand,
            List<ExtractionFunction> functions,
            List<Sort> rootSorts)
        {
            this.schema = schema;
            this.costs = costs;
            this.options = options;
            this.onDemand = onDemand;
            this.rootSorts = rootSorts.ToArray();
            egraph = schema.EGraph;
            createdAt = egraph.NextTimestamp;

            sorts = new List<Sort>();
            sortIndex = new Dictionary<Sort, int>(ReferenceEqualityComparer.Instance);
            sortNames = new Dictionary<string, Sort>(StringComparer.Ordinal);
            sortKinds = new List<SortKind>();
            sortCost = new List<long>();
            sortBaseCostFunction = new List<BaseValueCostFunction?>();
            sortContainerCostFunction = new List<ContainerCostFunction?>();
            sortProducers = new List<List<int>>();
            foreach (var sort in schema.Sorts)
                SortIndexOf(sort);

            foreach (var sort in this.rootSorts)
                SortIndexOf(sort);

            slots = functions.ToArray();
            slotColumnSorts = new int[slots.Length][];
            slotHeadCost = new long[slots.Length];
            slotHeadCostFunction = new NodeCostFunction?[slots.Length];
            slotVersions = new TableVersion[slots.Length];
            for (var slot = 0; slot < slots.Length; slot++)
            {
                var function = slots[slot];
                var columns = function.Columns;
                var columnSorts = new int[columns.Length];
                for (var i = 0; i < columns.Length; i++)
                    columnSorts[i] = SortIndexOf(columns[i]);

                slotColumnSorts[slot] = columnSorts;
                slotHeadCostFunction[slot] = costs.GetNodeCost(function.Id);
                slotHeadCost[slot] = HeadCost(function);
                slotVersions[slot] = egraph.Database.GetTable(function.Table).Version;
                sortProducers[columnSorts[function.OutputIndex]].Add(slot);
            }

            InitializeGraph();
            if (onDemand == false)
            {
                busy = true;
                ScanAll();
                RunKnuth();
                busy = false;
            }
        }

        /// <summary>
        /// Creates an extractor that costs every class of every sort reachable from <paramref name="rootSorts"/>: egglog's
        /// <c>compute_costs_from_rootsorts</c> (src/extract.rs:167-317). It reads every row of the constructors (and views)
        /// whose output sort is reachable, through key columns and container elements, from a root sort, and runs Knuth's
        /// algorithm over them all. O(rows read + occurrences + nodes log nodes).
        /// </summary>
        /// <param name="schema">The e-graph's extraction schema.</param>
        /// <param name="rootSorts">The sorts whose values will be extracted; <c>null</c> for the output sort of every
        /// extractable function, egglog's default.</param>
        /// <param name="costs">The cost model; <c>null</c> for <see cref="CostModel.Default"/>.</param>
        /// <param name="options">Which functions to use; <c>null</c> for <see cref="ExtractionOptions.Default"/>.</param>
        /// <returns>The extractor.</returns>
        /// <exception cref="InvalidOperationException">The cost model gave a negative cost.</exception>
        public static Extractor ComputeCostsFromRootSorts(
            ExtractionSchema schema,
            IReadOnlyList<Sort>? rootSorts = null,
            CostModel? costs = null,
            ExtractionOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(schema);
            options ??= ExtractionOptions.Default;

            // A reverse index from each output sort to the extractable functions producing it, in declaration order; with
            // no root sorts given, every such output sort is a root, in order of first appearance (extract.rs:211-244).
            var extractAll = rootSorts is null;
            var roots = new List<Sort>();
            if (rootSorts is not null)
                foreach (var sort in rootSorts)
                    roots.Add(sort ?? throw new ArgumentException("A root sort is null.", nameof(rootSorts)));

            var producers = new Dictionary<Sort, List<ExtractionFunction>>(ReferenceEqualityComparer.Instance);
            foreach (var function in schema.Functions)
            {
                if (IsUsable(function, options) == false)
                    continue;

                if (producers.TryGetValue(function.OutputSort, out var list))
                {
                    list.Add(function);
                }
                else
                {
                    producers.Add(function.OutputSort, [function]);
                    if (extractAll)
                        roots.Add(function.OutputSort);
                }
            }

            // Breadth-first search over sorts for the reachable functions (extract.rs:246-286).
            var queue = new Queue<Sort>();
            var seen = new HashSet<Sort>(ReferenceEqualityComparer.Instance);
            foreach (var root in roots)
                if (seen.Add(root))
                    queue.Enqueue(root);

            var reached = new HashSet<ExtractionFunction>(ReferenceEqualityComparer.Instance);
            var functions = new List<ExtractionFunction>();
            while (queue.TryDequeue(out var sort))
            {
                if (sort is ContainerSort container)
                {
                    foreach (var inner in container.InnerSorts)
                        if (seen.Add(inner))
                            queue.Enqueue(inner);
                }
                else if (sort.IsEqSort && producers.TryGetValue(sort, out var heads))
                {
                    foreach (var head in heads)
                    {
                        if (reached.Add(head) == false)
                            continue;

                        foreach (var child in head.Inputs.Slice(0, head.ChildCount))
                            if (seen.Add(child))
                                queue.Enqueue(child);

                        functions.Add(head);
                    }
                }
            }

            return new Extractor(schema, costs ?? CostModel.Default, options, false, functions, roots);
        }

        /// <summary>
        /// Creates an extractor that costs only the classes reachable from the values it is asked about (rule-layer
        /// decision D10), discovering them as queries arrive: a class's nodes are found by probing the cached index on the
        /// output column of each extractable function producing its sort, its children's classes are discovered in turn,
        /// and Knuth's algorithm then runs over what was discovered. Later queries extend the graph; classes already costed
        /// stay final. Any extractable function may be used, so any sort may be queried.
        /// </summary>
        /// <param name="schema">The e-graph's extraction schema.</param>
        /// <param name="costs">The cost model; <c>null</c> for <see cref="CostModel.Default"/>.</param>
        /// <param name="options">Which functions to use; <c>null</c> for <see cref="ExtractionOptions.Default"/>.</param>
        /// <returns>The extractor.</returns>
        public static Extractor CreateOnDemand(ExtractionSchema schema, CostModel? costs = null, ExtractionOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(schema);
            options ??= ExtractionOptions.Default;

            var functions = new List<ExtractionFunction>();
            foreach (var function in schema.Functions)
                if (IsUsable(function, options))
                    functions.Add(function);

            return new Extractor(schema, costs ?? CostModel.Default, options, true, functions, new List<Sort>());
        }

        /// <summary>
        /// Gets the schema the extractor reads.
        /// </summary>
        public ExtractionSchema Schema => schema;

        /// <summary>
        /// Gets the e-graph the extractor reads.
        /// </summary>
        public EGraph EGraph => egraph;

        /// <summary>
        /// Gets the cost model.
        /// </summary>
        public CostModel Costs => costs;

        /// <summary>
        /// Gets which functions the extractor may use.
        /// </summary>
        public ExtractionOptions Options => options;

        /// <summary>
        /// Gets whether the extractor discovers classes on demand (<see cref="CreateOnDemand"/>) rather than costing every
        /// class of its root sorts up front.
        /// </summary>
        public bool IsOnDemand => onDemand;

        /// <summary>
        /// Gets the root sorts of an extractor made by <see cref="ComputeCostsFromRootSorts"/>; empty for one made on
        /// demand.
        /// </summary>
        public IReadOnlyList<Sort> RootSorts => rootSorts;

        /// <summary>
        /// Gets the number of e-classes costed or being costed so far.
        /// </summary>
        internal int ClassCount => classCount;

        /// <summary>
        /// Gets the number of e-nodes read so far.
        /// </summary>
        internal int NodeCount => nodeCount;

        /// <summary>
        /// Gets the cost of the cheapest term of <paramref name="value"/>, a value of <paramref name="sort"/>: the class's
        /// cost for an e-class sort, the value's cost for a base sort, and the container's own cost plus its elements'
        /// for a container sort. egglog's <c>compute_cost_node</c> on the canonical value (src/extract.rs:319-339).
        /// </summary>
        /// <param name="sort">The value's sort.</param>
        /// <param name="value">The value; canonicalized for e-class and container sorts.</param>
        /// <param name="cost">The cost, when a finite term exists.</param>
        /// <returns>Whether the value has a finite term the extractor may use.</returns>
        /// <exception cref="ArgumentException">The extractor knows a different sort of the same name.</exception>
        /// <exception cref="InvalidOperationException">The e-graph changed since the extractor was created, the cost model
        /// gave a negative cost, or an earlier query failed.</exception>
        public bool TryGetCost(Sort sort, Value value, out long cost)
        {
            ArgumentNullException.ThrowIfNull(sort);
            Enter(sort);
            var found = TryResolve(sort, value, out _, out _, out cost);
            Exit();
            return found;
        }

        /// <summary>
        /// Extracts the cheapest term of <paramref name="value"/>, a value of <paramref name="sort"/>, into
        /// <paramref name="dag"/>. egglog's <c>extract_best_with_sort</c> (src/extract.rs:569-596).
        /// </summary>
        /// <param name="sort">The value's sort.</param>
        /// <param name="value">The value; canonicalized for e-class and container sorts.</param>
        /// <param name="dag">The DAG to extract into.</param>
        /// <param name="result">The term and its cost, when a finite term exists.</param>
        /// <returns>Whether the value has a finite term the extractor may use.</returns>
        /// <exception cref="ArgumentException">The extractor knows a different sort of the same name.</exception>
        /// <exception cref="InvalidOperationException">The e-graph changed since the extractor was created, the cost model
        /// gave a negative cost, or an earlier query failed.</exception>
        public bool TryExtractBest(Sort sort, Value value, TermDag dag, out ExtractionResult result)
        {
            ArgumentNullException.ThrowIfNull(sort);
            ArgumentNullException.ThrowIfNull(dag);
            Enter(sort);
            var found = TryExtractBestCore(sort, value, dag, null, out result);
            Exit();
            return found;
        }

        /// <summary>
        /// Extracts the cheapest term of <paramref name="value"/>, a value of the extractor's only root sort. egglog's
        /// <c>extract_best</c> (src/extract.rs:598-617).
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="dag">The DAG to extract into.</param>
        /// <param name="result">The term and its cost, when a finite term exists.</param>
        /// <returns>Whether the value has a finite term the extractor may use.</returns>
        /// <exception cref="InvalidOperationException">The extractor does not have exactly one root sort, the e-graph
        /// changed since it was created, the cost model gave a negative cost, or an earlier query failed.</exception>
        public bool TryExtractBest(Value value, TermDag dag, out ExtractionResult result)
        {
            return TryExtractBest(SingleRootSort(), value, dag, out result);
        }

        /// <summary>
        /// Extracts the cheapest term of <paramref name="value"/>, a value of <paramref name="sort"/>, as a tree of
        /// <see cref="ExtractedTerm"/>s that carry each subterm's sort, value and cost.
        /// </summary>
        /// <param name="sort">The value's sort.</param>
        /// <param name="value">The value; canonicalized for e-class and container sorts.</param>
        /// <param name="term">The term, when a finite term exists.</param>
        /// <returns>Whether the value has a finite term the extractor may use.</returns>
        /// <exception cref="ArgumentException">The extractor knows a different sort of the same name.</exception>
        /// <exception cref="InvalidOperationException">The e-graph changed since the extractor was created, the cost model
        /// gave a negative cost, or an earlier query failed.</exception>
        public bool TryExtractTerm(Sort sort, Value value, [NotNullWhen(true)] out ExtractedTerm? term)
        {
            ArgumentNullException.ThrowIfNull(sort);
            Enter(sort);
            term = null;
            if (TryResolve(sort, value, out var index, out var canonical, out _))
            {
                var builder = new ExtractedTermBuilder(this);
                term = Build<ExtractedTermBuilder, ExtractedTerm>(index, canonical, ref builder, new Dictionary<ulong, ExtractedTerm>());
            }

            Exit();
            return term is not null;
        }

        /// <summary>
        /// Extracts the cheapest term of <paramref name="value"/>, a value of <paramref name="sort"/>, as a tree of
        /// <see cref="ExtractedTerm"/>s.
        /// </summary>
        /// <param name="sort">The value's sort.</param>
        /// <param name="value">The value; canonicalized for e-class and container sorts.</param>
        /// <returns>The term.</returns>
        /// <exception cref="InvalidOperationException">The value has no finite term the extractor may use, the e-graph
        /// changed since the extractor was created, the cost model gave a negative cost, or an earlier query
        /// failed.</exception>
        public ExtractedTerm ExtractTerm(Sort sort, Value value)
        {
            if (TryExtractTerm(sort, value, out var term))
                return term;

            throw new InvalidOperationException($"Value {value} of sort {sort.Name} has no finite term the extractor may use.");
        }

        /// <summary>
        /// Gets the sort at <paramref name="index"/>.
        /// </summary>
        internal Sort SortAt(int index) => sorts[index];

        /// <summary>
        /// Gets the index of <paramref name="sort"/>, registering it (and resolving its costs) on first use. Fails before
        /// changing anything.
        /// </summary>
        /// <exception cref="ArgumentException">A different sort of the same name is registered, in the schema or
        /// here.</exception>
        internal int SortIndexOf(Sort sort)
        {
            if (sortIndex.TryGetValue(sort, out var index))
                return index;

            if (sortNames.ContainsKey(sort.Name))
                throw new ArgumentException($"Sort {sort.Name} is not the sort of that name this extractor knows.", nameof(sort));

            index = sorts.Count;
            sorts.Add(sort);
            sortIndex.Add(sort, index);
            sortNames.Add(sort.Name, sort);
            sortKinds.Add(sort.Kind);
            sortProducers.Add(new List<int>());
            switch (sort.Kind)
            {
                case SortKind.Base:
                    sortCost.Add(costs.GetBaseValueCost(sort, out var baseFunction));
                    sortBaseCostFunction.Add(baseFunction);
                    sortContainerCostFunction.Add(null);
                    break;
                case SortKind.Container:
                    sortCost.Add(costs.GetContainerCost(sort, out var containerFunction));
                    sortBaseCostFunction.Add(null);
                    sortContainerCostFunction.Add(containerFunction);
                    break;
                default:
                    sortCost.Add(0);
                    sortBaseCostFunction.Add(null);
                    sortContainerCostFunction.Add(null);
                    break;
            }

            return index;
        }

        /// <summary>
        /// Gets the cost of the base value <paramref name="value"/> of the base sort at <paramref name="sort"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">The cost model gave a negative cost.</exception>
        internal long BaseCost(int sort, uint value)
        {
            var function = sortBaseCostFunction[sort];
            if (function is null)
                return sortCost[sort];

            return CostMath.Check(function(sorts[sort], new Value(value)), "a value of", sorts[sort].Name);
        }

        /// <summary>
        /// Gets whether egglog would extract from <paramref name="function"/> under <paramref name="options"/>: a
        /// constructor or view that is not excluded as unextractable, hidden or a skipped view (extract.rs:219-231).
        /// </summary>
        static bool IsUsable(ExtractionFunction function, ExtractionOptions options)
        {
            if (function.Unextractable && options.RespectUnextractable)
                return false;

            if (function.IsView && options.SkipViewTables)
                return false;

            if (function.Hidden && options.RespectHidden)
                return false;

            return function.IsConstructor || function.IsView;
        }

        /// <summary>
        /// Gets the constant head cost of a function's rows (meaningless when a per-row cost function is given): the cost
        /// model's override, else for a view its term constructor's cost, else the declared cost, else 1. egglog's
        /// <c>extraction_head_cost</c> (src/extract.rs:759-772).
        /// </summary>
        long HeadCost(ExtractionFunction function)
        {
            if (costs.TryGetFunctionCost(function.Id, out var cost))
                return cost;

            if (function.TermConstructor is { } termConstructor)
            {
                if (costs.TryGetFunctionCost(termConstructor, out cost))
                    return cost;

                return schema.IsDeclared(termConstructor) ? schema.GetFunction(termConstructor).Cost ?? 1 : 1;
            }

            return function.Cost ?? 1;
        }

        /// <summary>
        /// Gets the only root sort, for egglog's single-sort conveniences.
        /// </summary>
        Sort SingleRootSort()
        {
            if (rootSorts.Length != 1)
                throw new InvalidOperationException($"This requires an extractor with a single root sort; it has {rootSorts.Length}.");

            return rootSorts[0];
        }

        /// <summary>
        /// Starts a query about values of <paramref name="sort"/>: checks the sort, then as <see cref="Enter()"/>.
        /// </summary>
        /// <exception cref="ArgumentException">A different sort of the same name is registered.</exception>
        /// <exception cref="InvalidOperationException">The extractor is stale or broken.</exception>
        void Enter(Sort sort)
        {
            SortIndexOf(sort);
            Enter();
        }

        /// <summary>
        /// Starts a query: checks that the e-graph has not changed and that no earlier query failed midway.
        /// </summary>
        /// <exception cref="InvalidOperationException">The extractor is stale or broken.</exception>
        void Enter()
        {
            if (busy)
                throw new InvalidOperationException(
                    "An earlier query of this extractor failed midway, leaving it unusable; create a new one.");

            if (egraph.NextTimestamp != createdAt)
                throw new InvalidOperationException("The e-graph changed since the extractor was created; create a new one.");

            for (var slot = 0; slot < slots.Length; slot++)
                if (egraph.Database.GetTable(slots[slot].Table).Version != slotVersions[slot])
                    throw new InvalidOperationException(
                        $"Function {slots[slot].Name} changed since the extractor was created; create a new one.");

            busy = true;
        }

        /// <summary>
        /// Ends a query that completed.
        /// </summary>
        void Exit()
        {
            busy = false;
        }

        /// <summary>
        /// Canonicalizes a value, makes sure what it depends on is costed, and gets its cost.
        /// </summary>
        /// <param name="sort">The value's sort.</param>
        /// <param name="value">The value.</param>
        /// <param name="index">The sort's index.</param>
        /// <param name="canonical">The canonical value.</param>
        /// <param name="cost">The cost, when a finite term exists.</param>
        /// <returns>Whether a finite term exists.</returns>
        bool TryResolve(Sort sort, Value value, out int index, out uint canonical, out long cost)
        {
            index = SortIndexOf(sort);
            canonical = sort.Kind == SortKind.Base ? value.Rep : egraph.GetCanonInUf(value).Rep;
            switch (sort.Kind)
            {
                case SortKind.Base:
                    cost = BaseCost(index, canonical);
                    return true;
                case SortKind.Eq:
                {
                    var found = onDemand ? GetOrAddClass(index, canonical) : FindClass(index, canonical);
                    if (onDemand)
                        Settle();

                    if (found >= 0 && classBest[found] >= 0)
                    {
                        cost = classCost[found];
                        return true;
                    }

                    cost = 0;
                    return false;
                }
                default:
                {
                    var container = GetContainer(index, canonical);
                    if (onDemand)
                        Settle();

                    return TryGetContainerCost(container, out cost);
                }
            }
        }

        /// <summary>
        /// Extracts the cheapest term of a value into a DAG, sharing <paramref name="memo"/> when given.
        /// </summary>
        bool TryExtractBestCore(Sort sort, Value value, TermDag dag, Dictionary<ulong, TermId>? memo, out ExtractionResult result)
        {
            if (TryResolve(sort, value, out var index, out var canonical, out var cost) == false)
            {
                result = default;
                return false;
            }

            var builder = new TermDagBuilder(this, dag);
            var term = Build<TermDagBuilder, TermId>(index, canonical, ref builder, memo ?? new Dictionary<ulong, TermId>());
            result = new ExtractionResult(cost, term);
            return true;
        }

    }

}
