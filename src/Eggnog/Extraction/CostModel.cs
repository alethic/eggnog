using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// What extraction minimizes: a head cost per constructor (default: its declared cost), optionally a per-row cost
    /// function, and value costs (default 1, egglog's <c>base_value_cost</c>); a term costs its heads' and values' costs
    /// summed. All costs must be at least 0. Immutable and thread-safe: every <c>With</c> returns a new model.
    /// </summary>
    public sealed class CostModel
    {

        readonly Dictionary<ConstructorHandle, ulong> headCosts;
        readonly Dictionary<ConstructorHandle, NodeCostFunction> nodeCosts;
        readonly Dictionary<ColumnType, ulong> valueCosts;
        readonly Dictionary<ColumnType, Func<uint, ulong>> valueCostFunctions;
        readonly HashSet<ConstructorHandle> unextractable;

        /// <summary>
        /// Initializes the default model.
        /// </summary>
        CostModel()
        {
            headCosts = new Dictionary<ConstructorHandle, ulong>(ReferenceEqualityComparer.Instance);
            nodeCosts = new Dictionary<ConstructorHandle, NodeCostFunction>(ReferenceEqualityComparer.Instance);
            valueCosts = new Dictionary<ColumnType, ulong>(ReferenceEqualityComparer.Instance);
            valueCostFunctions = new Dictionary<ColumnType, Func<uint, ulong>>(ReferenceEqualityComparer.Instance);
            unextractable = new HashSet<ConstructorHandle>(ReferenceEqualityComparer.Instance);
        }

        /// <summary>
        /// Initializes a copy of <paramref name="other"/>.
        /// </summary>
        CostModel(CostModel other)
        {
            headCosts = new Dictionary<ConstructorHandle, ulong>(other.headCosts, ReferenceEqualityComparer.Instance);
            nodeCosts = new Dictionary<ConstructorHandle, NodeCostFunction>(other.nodeCosts, ReferenceEqualityComparer.Instance);
            valueCosts = new Dictionary<ColumnType, ulong>(other.valueCosts, ReferenceEqualityComparer.Instance);
            valueCostFunctions = new Dictionary<ColumnType, Func<uint, ulong>>(other.valueCostFunctions, ReferenceEqualityComparer.Instance);
            unextractable = new HashSet<ConstructorHandle>(other.unextractable, ReferenceEqualityComparer.Instance);
        }

        /// <summary>
        /// Gets the default model: declared constructor costs and 1 per value.
        /// </summary>
        public static CostModel Default { get; } = new();

        /// <summary>
        /// Gets a model in which a node of <paramref name="head"/> costs <paramref name="cost"/>, without its children.
        /// </summary>
        /// <param name="head">The constructor.</param>
        /// <param name="cost">The cost, at least 0.</param>
        /// <returns>The new model.</returns>
        public CostModel With(ConstructorHandle head, ulong cost)
        {
            ArgumentNullException.ThrowIfNull(head);
            var model = new CostModel(this);
            model.nodeCosts.Remove(head);
            model.headCosts[head] = cost;
            return model;
        }

        /// <summary>
        /// Gets a model in which a node of <paramref name="head"/> costs what <paramref name="cost"/> says for its row,
        /// without its children.
        /// </summary>
        /// <param name="head">The constructor.</param>
        /// <param name="cost">The per-row cost: pure; it must not call into any graph.</param>
        /// <returns>The new model.</returns>
        public CostModel With(ConstructorHandle head, NodeCostFunction cost)
        {
            ArgumentNullException.ThrowIfNull(head);
            ArgumentNullException.ThrowIfNull(cost);
            var model = new CostModel(this);
            model.headCosts.Remove(head);
            model.nodeCosts[head] = cost;
            return model;
        }

        /// <summary>
        /// Gets a model in which every value of <paramref name="type"/> costs <paramref name="cost"/>.
        /// </summary>
        /// <param name="type">A value type.</param>
        /// <param name="cost">The cost, at least 0.</param>
        /// <returns>The new model.</returns>
        /// <exception cref="ArgumentException"><paramref name="type"/> is a sort, or a container, which costs its
        /// elements' costs.</exception>
        public CostModel WithValueCost(ColumnType type, ulong cost)
        {
            CheckValueType(type);
            var model = new CostModel(this);
            model.valueCostFunctions.Remove(type);
            model.valueCosts[type] = cost;
            return model;
        }

        /// <summary>
        /// Gets a model in which a value of <paramref name="type"/> costs what <paramref name="cost"/> says for it.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="type">A value type.</param>
        /// <param name="cost">The per-value cost: pure; it must not call into any graph.</param>
        /// <returns>The new model.</returns>
        /// <exception cref="ArgumentException"><paramref name="type"/> is a sort, or a container, which costs its
        /// elements' costs.</exception>
        public CostModel WithValueCost<T>(ColumnType<T> type, Func<T, ulong> cost)
        {
            CheckValueType(type);
            ArgumentNullException.ThrowIfNull(cost);
            var model = new CostModel(this);
            model.valueCosts.Remove(type);
            model.valueCostFunctions[type] = cell => cost(type.Decode(cell, null));
            return model;
        }

        /// <summary>
        /// Gets a model under which extraction may not use <paramref name="head"/>'s rows.
        /// </summary>
        /// <param name="head">The constructor.</param>
        /// <returns>The new model.</returns>
        public CostModel Unextractable(ConstructorHandle head)
        {
            ArgumentNullException.ThrowIfNull(head);
            var model = new CostModel(this);
            model.unextractable.Add(head);
            return model;
        }

        /// <summary>
        /// Gets the head costs.
        /// </summary>
        internal IReadOnlyDictionary<ConstructorHandle, ulong> HeadCosts => headCosts;

        /// <summary>
        /// Gets the per-row cost functions.
        /// </summary>
        internal IReadOnlyDictionary<ConstructorHandle, NodeCostFunction> NodeCosts => nodeCosts;

        /// <summary>
        /// Gets the value costs.
        /// </summary>
        internal IReadOnlyDictionary<ColumnType, ulong> ValueCosts => valueCosts;

        /// <summary>
        /// Gets the per-value cost functions, over cells.
        /// </summary>
        internal IReadOnlyDictionary<ColumnType, Func<uint, ulong>> ValueCostFunctions => valueCostFunctions;

        /// <summary>
        /// Gets the heads extraction may not use under this model.
        /// </summary>
        internal IReadOnlyCollection<ConstructorHandle> UnextractableHeads => unextractable;

        /// <summary>
        /// Gets whether this model marks a head unextractable (<see cref="Unextractable"/>).
        /// </summary>
        /// <param name="head">The head.</param>
        /// <returns>Whether it is marked.</returns>
        internal bool IsUnextractable(ConstructorHandle head) => unextractable.Contains(head);

        /// <summary>
        /// Checks that a type can have a value cost.
        /// </summary>
        static void CheckValueType(ColumnType type)
        {
            ArgumentNullException.ThrowIfNull(type);
            if (type.Kind == ColumnKind.Sort)
                throw new ArgumentException($"{type.Name} is a sort; only values have value costs.", nameof(type));

            if (type is IContainerColumnType)
                throw new ArgumentException(
                    $"{type.Name} is a container, which costs the sum of its elements' costs, as egglog's containers do; give its element types value costs.",
                    nameof(type));
        }

    }

}
