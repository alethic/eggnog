using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog.Relations
{

    /// <summary>
    /// Finds the cheapest term of e-classes of an <see cref="EGraph"/>: the port of egglog's <c>Extractor</c>
    /// (src/extract.rs:126-757), computing costs with Knuth's algorithm instead of egglog's Bellman-Ford fixpoint, and
    /// choosing among nodes of equal cost as egglog's passes do.
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
    /// its exact cost, and the first node of a class to leave the queue makes the class final. Sums of costs
    /// are superior functions, for which this is optimal, and the costs are the fixpoint egglog's Bellman-Ford reaches.
    /// </para>
    /// <para>
    /// Ties are egglog's. A class extracts as the row egglog's extractor builds it from: the first row, with functions in
    /// egglog's order and each function's rows in storage order, that costs exactly the class's cost and whose children
    /// all reached their own costs before the class reached its in egglog's passes, which keeps the term finite when
    /// costs are zero. When those moments fall follows from the costs, without running the passes, by a second run of
    /// Knuth's algorithm over the rows that cost exactly their class's cost (Extractor.Choices.cs). The functions are
    /// ordered breadth-first from the extractor's root sorts, and on demand from the queried value's sort, as egglog's
    /// extract command roots its extractor at the sort of what it extracts.
    /// </para>
    /// <para>
    /// Two ways to create one. <see cref="ComputeCostsFromRootSorts"/> is egglog's: it reads every row of every
    /// constructor reachable from the root sorts and costs every class at once.
    /// <see cref="CreateOnDemand(ExtractionSchema, CostModel?, ExtractionOptions?)"/> reads only the classes reachable
    /// from the values it is asked about (Knuth's algorithm over the classes reachable from them), finding each class's
    /// nodes through the cached index on its constructors' output columns, and extends its graph as later queries reach
    /// further; classes already costed stay final, because each discovery is closed downward, and a class's choice
    /// depends only on the rows below it, so each query is answered as a fresh extractor rooted at its sort answers it,
    /// whatever was asked before.
    /// </para>
    /// <para>
    /// Contexts. <see cref="CreateOnDemand(ExtractionSchema, EGraphContext, CostModel?, ExtractionOptions?)"/> extracts
    /// in an <see cref="EGraphContext"/>: classes are canonical in the context, and a class's nodes are the rows the
    /// context sees, found through each layer's cached index on the output column with the rows the context hides
    /// filtered out, never through the root's tables alone, and ordered as the context's scan meets them: the root's
    /// layer, then each ancestor's in depth order, then its own. The root forms read the root's tables alone, at no
    /// extra cost per node.
    /// </para>
    /// <para>
    /// The extractor reads the e-graph as it was when created, which must be rebuilt (canonical), as it is after every
    /// flush and run. Using it after what it reads changed throws: the rows of its functions' tables, or the
    /// union-find, at any level of its context's chain that was not frozen when it was created
    /// (<see cref="ExtractionWatch"/>). The clock is not consulted, so rule runs, queries and write batches that change
    /// none of that keep it current. Not thread-safe: queries extend its caches. Extractors over a frozen context share
    /// nothing mutable, so any number of them may run at once, each on its own thread.
    /// </para>
    /// </remarks>
    internal sealed partial class Extractor
    {

        readonly ExtractionSchema schema;
        readonly EGraph egraph;

        /// <summary>
        /// The context of an extractor made through <see cref="CreateOnDemand(ExtractionSchema, EGraphContext, CostModel?,
        /// ExtractionOptions?)"/>, whose thread domain each query enters; <c>null</c> for one made through the root API,
        /// which reads the root as egglog does.
        /// </summary>
        readonly EGraphContext? host;

        /// <summary>
        /// The layers of a child context, through whose views discovery reads; <c>null</c> at the root.
        /// </summary>
        readonly ContextTables? layers;

        /// <summary>
        /// The find of a child context; <c>null</c> at the root, which canonicalizes through the root's union-find.
        /// </summary>
        readonly LayeredUnionFind? finder;

        readonly CostModel costs;
        readonly ExtractionOptions options;
        readonly bool onDemand;
        readonly Sort[] rootSorts;

        readonly ExtractionFunction[] slots;
        readonly int[][] slotColumnSorts;
        readonly ulong[] slotHeadCost;
        readonly NodeCostFunction?[] slotHeadCostFunction;

        /// <summary>
        /// The staleness check: the versions of the slots' tables and of the union-find at the watched levels.
        /// </summary>
        readonly ExtractionWatch watch;

        readonly List<Sort> sorts;
        readonly Dictionary<Sort, int> sortIndex;
        readonly Dictionary<string, Sort> sortNames;
        readonly List<SortKind> sortKinds;
        readonly List<ulong> sortCost;
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
        /// <param name="schema">The extraction schema.</param>
        /// <param name="costs">The cost model.</param>
        /// <param name="options">Which functions to use.</param>
        /// <param name="onDemand">Whether to discover classes on demand rather than scan.</param>
        /// <param name="functions">The functions to read, in declaration order.</param>
        /// <param name="rootSorts">The root sorts of a scanning extractor.</param>
        /// <param name="host">The context, for the context form; <c>null</c> for the root API. When it is not frozen, the
        /// caller holds its thread domain.</param>
        Extractor(
            ExtractionSchema schema,
            CostModel costs,
            ExtractionOptions options,
            bool onDemand,
            List<ExtractionFunction> functions,
            List<Sort> rootSorts,
            EGraphContext? host)
        {
            this.schema = schema;
            this.costs = costs;
            this.options = options;
            this.onDemand = onDemand;
            this.rootSorts = rootSorts.ToArray();
            this.host = host;
            egraph = schema.EGraph;
            layers = host?.Context.Tables;
            finder = layers is null ? null : host!.Context.UnionFind;

            sorts = new List<Sort>();
            sortIndex = new Dictionary<Sort, int>(ReferenceEqualityComparer.Instance);
            sortNames = new Dictionary<string, Sort>(StringComparer.Ordinal);
            sortKinds = new List<SortKind>();
            sortCost = new List<ulong>();
            sortBaseCostFunction = new List<BaseValueCostFunction?>();
            sortContainerCostFunction = new List<ContainerCostFunction?>();
            sortProducers = new List<List<int>>();
            foreach (var sort in schema.Sorts)
                SortIndexOf(sort);

            foreach (var sort in this.rootSorts)
                SortIndexOf(sort);

            slots = functions.ToArray();
            slotColumnSorts = new int[slots.Length][];
            slotHeadCost = new ulong[slots.Length];
            slotHeadCostFunction = new NodeCostFunction?[slots.Length];
            var slotTables = new TableId[slots.Length];
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
                slotTables[slot] = function.Table;
                sortProducers[columnSorts[function.OutputIndex]].Add(slot);
            }

            watch = new ExtractionWatch((host ?? egraph.Root).Context, slotTables);
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

            return new Extractor(schema, costs ?? CostModel.Default, options, false, functions, roots, null);
        }

        /// <summary>
        /// Creates an extractor that costs only the classes reachable from the values it is asked about, discovering
        /// them as queries arrive: a class's nodes are found by probing the cached index on the output column of each
        /// extractable function producing its sort, its children's classes are discovered in turn, and Knuth's
        /// algorithm then runs over what was discovered. Later queries extend the graph; classes already costed stay
        /// final. Any extractable function may be used, so any sort may be queried.
        /// </summary>
        /// <param name="schema">The e-graph's extraction schema.</param>
        /// <param name="costs">The cost model; <c>null</c> for <see cref="CostModel.Default"/>.</param>
        /// <param name="options">Which functions to use; <c>null</c> for <see cref="ExtractionOptions.Default"/>.</param>
        /// <returns>The extractor.</returns>
        public static Extractor CreateOnDemand(ExtractionSchema schema, CostModel? costs = null, ExtractionOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(schema);
            options ??= ExtractionOptions.Default;
            return new Extractor(schema, costs ?? CostModel.Default, options, true, UsableFunctions(schema, options), new List<Sort>(), null);
        }

        /// <summary>
        /// Creates an extractor over <paramref name="context"/> that costs only the classes reachable from the values
        /// it is asked about, as <see cref="CreateOnDemand(ExtractionSchema, CostModel?, ExtractionOptions?)"/> does at
        /// the root: classes are canonical in the context, and a class's nodes are the rows the context sees whose
        /// output column holds it, found by probing, in each layer of the context's view of each producing function,
        /// the layer's cached index on the output column, with the rows the context hides skipped. At the root it reads
        /// what the root API's extractor reads and chooses the same terms.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The context must be clean (<see cref="EGraphContext.IsClean"/>; call <see cref="EGraphContext.EnsureClean"/>
        /// first). Each query enters the context's thread domain while it is not frozen, and refuses to run once the rows
        /// of an extractable function's table or the union-find changed in the context or in an ancestor that was not
        /// frozen at the creation (<see cref="ExtractionWatch"/>); the clock is not consulted.
        /// </para>
        /// <para>
        /// On a frozen context any number of extractors may run at once, one per thread: a frozen chain never changes, its
        /// views and its finds are safe to read from any thread, and the cached indexes of its layers are built
        /// thread-safely on first use.
        /// </para>
        /// </remarks>
        /// <param name="schema">The e-graph's extraction schema.</param>
        /// <param name="context">A context of the schema's e-graph.</param>
        /// <param name="costs">The cost model; <c>null</c> for <see cref="CostModel.Default"/>.</param>
        /// <param name="options">Which functions to use; <c>null</c> for <see cref="ExtractionOptions.Default"/>.</param>
        /// <returns>The extractor.</returns>
        /// <exception cref="ArgumentException">The context belongs to another e-graph.</exception>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The context is faulted, stopped or not clean, or its domain is held
        /// by another thread.</exception>
        public static Extractor CreateOnDemand(ExtractionSchema schema, EGraphContext context, CostModel? costs = null, ExtractionOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(context);
            if (ReferenceEquals(context.Owner, schema.EGraph) == false)
                throw new ArgumentException("The context belongs to another e-graph than the extraction schema.", nameof(context));

            options ??= ExtractionOptions.Default;
            var functions = UsableFunctions(schema, options);
            using var scope = context.EnterExtraction(requireClean: true);
            return new Extractor(schema, costs ?? CostModel.Default, options, true, functions, new List<Sort>(), context);
        }

        /// <summary>
        /// Lists the functions of <paramref name="schema"/> an extractor may use under <paramref name="options"/>, in
        /// declaration order.
        /// </summary>
        static List<ExtractionFunction> UsableFunctions(ExtractionSchema schema, ExtractionOptions options)
        {
            var functions = new List<ExtractionFunction>();
            foreach (var function in schema.Functions)
                if (IsUsable(function, options))
                    functions.Add(function);

            return functions;
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
        /// Gets the context the extractor reads: the one it was created over, or the e-graph's root.
        /// </summary>
        public EGraphContext Context => host ?? egraph.Root;

        /// <summary>
        /// Gets the cost model.
        /// </summary>
        public CostModel Costs => costs;

        /// <summary>
        /// Gets which functions the extractor may use.
        /// </summary>
        public ExtractionOptions Options => options;

        /// <summary>
        /// Gets whether the extractor discovers classes on demand (<see cref="CreateOnDemand(ExtractionSchema, CostModel?,
        /// ExtractionOptions?)"/>, or its context form) rather than costing every class of its root sorts up front.
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
        /// <exception cref="InvalidOperationException">The e-graph changed since the extractor was created, or an earlier
        /// query failed.</exception>
        public bool TryGetCost(Sort sort, Value value, out ulong cost)
        {
            ArgumentNullException.ThrowIfNull(sort);
            using var scope = Enter(sort);
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
        /// <exception cref="InvalidOperationException">The e-graph changed since the extractor was created, or an earlier
        /// query failed.</exception>
        public bool TryExtractBest(Sort sort, Value value, TermDag dag, out ExtractionResult result)
        {
            ArgumentNullException.ThrowIfNull(sort);
            ArgumentNullException.ThrowIfNull(dag);
            using var scope = Enter(sort);
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
        /// changed since it was created, or an earlier query failed.</exception>
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
        /// <exception cref="InvalidOperationException">The e-graph changed since the extractor was created, or an earlier
        /// query failed.</exception>
        public bool TryExtractTerm(Sort sort, Value value, [NotNullWhen(true)] out ExtractedTerm? term)
        {
            ArgumentNullException.ThrowIfNull(sort);
            using var scope = Enter(sort);
            term = null;
            if (TryResolve(sort, value, out var index, out var canonical, out _))
            {
                var order = OrderFor(index);
                EnsureChoices(order, index, canonical);
                var builder = new ExtractedTermBuilder(this);
                var memo = new Dictionary<ulong, ExtractedTerm>();
                term = Build<ExtractedTermBuilder, ExtractedTerm>(order, index, canonical, ref builder, memo);
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
        /// changed since the extractor was created, or an earlier query failed.</exception>
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
        internal ulong BaseCost(int sort, uint value)
        {
            var function = sortBaseCostFunction[sort];
            if (function is null)
                return sortCost[sort];

            return function(sorts[sort], new Value(value));
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
        ulong HeadCost(ExtractionFunction function)
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
        /// <returns>The query's hold on the context's thread domain, to dispose when the query ends.</returns>
        /// <exception cref="ArgumentException">A different sort of the same name is registered.</exception>
        /// <exception cref="InvalidOperationException">The extractor is stale or broken.</exception>
        ExtractionQueryScope Enter(Sort sort)
        {
            SortIndexOf(sort);
            return Enter();
        }

        /// <summary>
        /// Starts a query: enters the context's thread domain (context form, context not frozen), and checks that no
        /// earlier query failed midway and that nothing the extractor reads changed (<see cref="ExtractionWatch"/>).
        /// O(watched levels × slots).
        /// </summary>
        /// <returns>The query's hold on the context's thread domain, to dispose when the query ends.</returns>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The extractor is stale or broken, the context is faulted or stopped,
        /// or its domain is held by another thread.</exception>
        ExtractionQueryScope Enter()
        {
            if (busy)
                throw new InvalidOperationException(
                    "An earlier query of this extractor failed midway, leaving it unusable; create a new one.");

            var scope = host is null ? default : host.EnterExtraction(requireClean: false);
            var change = watch.FindChange();
            if (change != ExtractionWatch.NoChange)
            {
                scope.Dispose();
                if (change == ExtractionWatch.UnionFindChanged)
                    throw new InvalidOperationException("The e-graph changed since the extractor was created; create a new one.");

                throw new InvalidOperationException(
                    $"Function {slots[change].Name} changed since the extractor was created; create a new one.");
            }

            busy = true;
            return scope;
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
        bool TryResolve(Sort sort, Value value, out int index, out uint canonical, out ulong cost)
        {
            index = SortIndexOf(sort);
            canonical = sort.Kind == SortKind.Base ? value.Rep : Canonical(value.Rep);
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

                    if (found >= 0 && classKnown[found])
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
        /// Gets the canonical id of <paramref name="id"/> in the context the extractor reads: the root's union-find, or the
        /// child's find. Once per query value, never per node.
        /// </summary>
        uint Canonical(uint id) => finder is { } layered ? layered.Find(id) : egraph.GetCanonInUf(new Value(id)).Rep;

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

            var order = OrderFor(index);
            EnsureChoices(order, index, canonical);
            var builder = new TermDagBuilder(this, dag);
            var term = Build<TermDagBuilder, TermId>(order, index, canonical, ref builder, memo ?? new Dictionary<ulong, TermId>());
            result = new ExtractionResult(cost, term);
            return true;
        }

    }

}
