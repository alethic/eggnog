using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Eggnog
{

    /// <summary>
    /// The graph's extraction bookkeeping (§6.10): one engine extraction schema per distinct set of heads that cost models
    /// mark unextractable (the empty set is the common case), every constructor declared in each; the map from engine
    /// functions to constructors; and the conversion of cost models, cached per model instance.
    /// </summary>
    /// <remarks>
    /// Relations, functions and (from R2) mirror tables are never declared. Thread-safe: schemas are made and extended
    /// under the graph's engine lock, then the catalog's lock.
    /// </remarks>
    internal sealed class ExtractionCatalog
    {

        readonly EGraph graph;
        readonly object sync = new();
        readonly List<ConstructorHandle> constructors = new();
        readonly Dictionary<int, ConstructorHandle> byFunction = new();
        readonly Dictionary<string, (Relations.ExtractionSchema Schema, HashSet<ConstructorHandle> Unextractable)> schemas = new(StringComparer.Ordinal);
        readonly ConditionalWeakTable<CostModel, Relations.CostModel> engineCosts = new();

        /// <summary>
        /// Initializes an empty catalog.
        /// </summary>
        /// <param name="graph">The graph.</param>
        public ExtractionCatalog(EGraph graph)
        {
            this.graph = graph;
        }

        /// <summary>
        /// Declares a constructor in every cached schema. Called under the definition and engine locks.
        /// </summary>
        /// <param name="constructor">The constructor.</param>
        public void Declare(ConstructorHandle constructor)
        {
            lock (sync)
            {
                constructors.Add(constructor);
                byFunction.Add(constructor.Function.Rep, constructor);
                foreach (var (schema, unextractable) in schemas.Values)
                    DeclareIn(schema, constructor, unextractable);
            }
        }

        /// <summary>
        /// Gets the schema for a cost model: every constructor declared, unextractable where the constructor says so or the
        /// model marks it.
        /// </summary>
        /// <param name="costs">The cost model.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="ArgumentException">The model marks a constructor of another graph.</exception>
        public Relations.ExtractionSchema SchemaFor(CostModel costs)
        {
            var marked = new HashSet<ConstructorHandle>(ReferenceEqualityComparer.Instance);
            foreach (var head in costs.UnextractableHeads)
            {
                CheckHead(head);
                marked.Add(head);
            }

            var key = string.Join(",", marked.Select(h => h.Function.Rep).OrderBy(r => r));
            lock (graph.EngineLock)
            {
                lock (sync)
                {
                    if (schemas.TryGetValue(key, out var cached))
                        return cached.Schema;

                    var schema = new Relations.ExtractionSchema(graph.Engine);
                    foreach (var constructor in constructors)
                        DeclareIn(schema, constructor, marked);

                    schemas.Add(key, (schema, marked));
                    return schema;
                }
            }
        }

        /// <summary>
        /// Gets the engine cost model of a cost model (cached per instance): head costs, per-row costs (whose rows a
        /// <see cref="NodeView"/> decodes, R1: with the root's origin) and value costs, every user delegate invoked under
        /// <see cref="EngineCallMarker"/>.
        /// </summary>
        /// <param name="costs">The cost model.</param>
        /// <returns>The engine cost model.</returns>
        /// <exception cref="ArgumentException">The model names a constructor or column type of another graph.</exception>
        public Relations.CostModel EngineCosts(CostModel costs) => engineCosts.GetValue(costs, Convert);

        /// <summary>
        /// Gets the constructor of an engine function.
        /// </summary>
        /// <param name="function">The engine function.</param>
        /// <returns>The constructor.</returns>
        public ConstructorHandle HeadOf(Relations.FunctionId function)
        {
            lock (sync)
                return byFunction[function.Rep];
        }

        /// <summary>
        /// Gets the extraction sort of a column type.
        /// </summary>
        /// <param name="type">The column type.</param>
        /// <returns>The sort.</returns>
        public Relations.Sort SortOf(ColumnType type) => type.ExtractionSort;

        /// <summary>
        /// Converts a cost model.
        /// </summary>
        Relations.CostModel Convert(CostModel costs)
        {
            var model = Relations.CostModel.Default;
            foreach (var (head, cost) in costs.HeadCosts)
            {
                CheckHead(head);
                model = model.WithFunctionCost(head.Function, cost);
            }

            foreach (var (head, function) in costs.NodeCosts)
            {
                CheckHead(head);
                var constructor = head;
                var root = graph.Root;
                model = model.WithNodeCost(head.Function, (_, row) =>
                {
                    EngineCallMarker.Enter();
                    try
                    {
                        return function(new NodeView(constructor, row, root));
                    }
                    finally
                    {
                        EngineCallMarker.Exit();
                    }
                });
            }

            foreach (var (type, cost) in costs.ValueCosts)
            {
                CheckType(type);
                model = model.WithBaseValueCost(SortOf(type), cost);
            }

            foreach (var (type, function) in costs.ValueCostFunctions)
            {
                CheckType(type);
                model = model.WithBaseValueCost(SortOf(type), (_, value) =>
                {
                    EngineCallMarker.Enter();
                    try
                    {
                        return function(value.Rep);
                    }
                    finally
                    {
                        EngineCallMarker.Exit();
                    }
                });
            }

            return model;
        }

        /// <summary>
        /// Declares a constructor in a schema.
        /// </summary>
        void DeclareIn(Relations.ExtractionSchema schema, ConstructorHandle constructor, HashSet<ConstructorHandle> unextractable)
        {
            var inputs = new Relations.Sort[constructor.KeyArray.Length];
            for (var i = 0; i < inputs.Length; i++)
                inputs[i] = SortOf(constructor.KeyArray[i]);

            schema.Declare(constructor.Function, new Relations.FunctionSignature
            {
                Inputs = inputs,
                Output = SortOf(constructor.Output!),
                IsConstructor = true,
                Cost = constructor.Cost,
                Unextractable = constructor.Extractable == false || unextractable.Contains(constructor),
            });
        }

        /// <summary>
        /// Checks that a constructor belongs to the graph.
        /// </summary>
        void CheckHead(ConstructorHandle head)
        {
            if (ReferenceEquals(head.Graph, graph) == false)
                throw new ArgumentException($"Constructor '{head.Name}' of the cost model belongs to another graph.");
        }

        /// <summary>
        /// Checks that a column type belongs to the graph and is a value type extraction sees.
        /// </summary>
        void CheckType(ColumnType type)
        {
            if (ReferenceEquals(type.Store, graph.Values) == false || type is UnitColumnType)
                throw new ArgumentException($"Column type {type.Name} of the cost model belongs to another graph.");
        }

    }

}
