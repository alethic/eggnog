using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// How extraction costs terms: egglog's <c>TreeAdditiveCostModel</c> (src/extract.rs:102-124) with egglog's
    /// <c>CostModel</c> hooks (src/extract.rs:16-50) as overrides. A term's cost is the saturating sum of its head's cost
    /// and its children's costs; a base value costs 1 unless overridden; a container costs its own cost (0 unless
    /// overridden) plus its elements' costs. All costs are non-negative <see cref="long"/>s (rule-layer decision D19).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A head's cost is, in order of precedence: a <see cref="NodeCostFunction"/> given for its function
    /// (<see cref="WithNodeCost"/>, egglog's per-row <c>enode_cost</c>); a constant given for its function
    /// (<see cref="WithFunctionCost"/>); for a view, the cost of its term constructor; and otherwise the function's
    /// declared <see cref="FunctionSignature.Cost"/>, 1 when it declares none (egglog's <c>extraction_head_cost</c>,
    /// src/extract.rs:759-772).
    /// </para>
    /// <para>
    /// egglog's <c>fold</c> hook, which may combine a head's and its children's costs arbitrarily, is fixed to the sum:
    /// the extractor runs Knuth's algorithm (rule-layer decision D10), which is optimal for superior functions (a term
    /// costs at least as much as each subterm, and more when a subterm costs more), and the saturating sum of
    /// non-negative costs is one. For the same reason a container's cost is its own cost plus its elements', not an
    /// arbitrary function of them.
    /// </para>
    /// <para>
    /// Immutable: every <c>With</c> method returns a new model. Thread-safe.
    /// </para>
    /// </remarks>
    internal sealed class CostModel
    {

        readonly Dictionary<FunctionId, long> functionCosts;
        readonly Dictionary<FunctionId, NodeCostFunction> nodeCosts;
        readonly Dictionary<Sort, long> baseCosts;
        readonly Dictionary<Sort, BaseValueCostFunction> baseCostFunctions;
        readonly Dictionary<Sort, long> containerCosts;
        readonly Dictionary<Sort, ContainerCostFunction> containerCostFunctions;

        /// <summary>
        /// Initializes the default model.
        /// </summary>
        CostModel()
        {
            functionCosts = new Dictionary<FunctionId, long>();
            nodeCosts = new Dictionary<FunctionId, NodeCostFunction>();
            baseCosts = new Dictionary<Sort, long>(ReferenceEqualityComparer.Instance);
            baseCostFunctions = new Dictionary<Sort, BaseValueCostFunction>(ReferenceEqualityComparer.Instance);
            containerCosts = new Dictionary<Sort, long>(ReferenceEqualityComparer.Instance);
            containerCostFunctions = new Dictionary<Sort, ContainerCostFunction>(ReferenceEqualityComparer.Instance);
            DefaultBaseValueCost = 1;
        }

        /// <summary>
        /// Initializes a copy of <paramref name="other"/>, to modify before publishing.
        /// </summary>
        CostModel(CostModel other)
        {
            functionCosts = new Dictionary<FunctionId, long>(other.functionCosts);
            nodeCosts = new Dictionary<FunctionId, NodeCostFunction>(other.nodeCosts);
            baseCosts = new Dictionary<Sort, long>(other.baseCosts, ReferenceEqualityComparer.Instance);
            baseCostFunctions = new Dictionary<Sort, BaseValueCostFunction>(other.baseCostFunctions, ReferenceEqualityComparer.Instance);
            containerCosts = new Dictionary<Sort, long>(other.containerCosts, ReferenceEqualityComparer.Instance);
            containerCostFunctions = new Dictionary<Sort, ContainerCostFunction>(
                other.containerCostFunctions,
                ReferenceEqualityComparer.Instance);
            DefaultBaseValueCost = other.DefaultBaseValueCost;
        }

        /// <summary>
        /// Gets egglog's default cost model, <c>TreeAdditiveCostModel</c>: declared head costs (1 by default), base values
        /// cost 1, containers cost the sum of their elements.
        /// </summary>
        public static CostModel Default { get; } = new();

        /// <summary>
        /// Gets the cost of a base value whose sort has no override: 1 unless changed by
        /// <see cref="WithDefaultBaseValueCost"/>. egglog's <c>base_value_cost</c> default, <c>C::unit()</c>.
        /// </summary>
        public long DefaultBaseValueCost { get; private set; }

        /// <summary>
        /// Returns a model in which every row of <paramref name="function"/> costs <paramref name="cost"/> (without its
        /// children), replacing any cost given for the function before.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="cost">The head cost; at least 0.</param>
        /// <returns>The new model.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is negative.</exception>
        public CostModel WithFunctionCost(FunctionId function, long cost)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(cost);

            var model = new CostModel(this);
            model.nodeCosts.Remove(function);
            model.functionCosts[function] = cost;
            return model;
        }

        /// <summary>
        /// Returns a model in which the rows of <paramref name="function"/> cost what <paramref name="cost"/> says
        /// (without their children), replacing any cost given for the function before: egglog's per-row
        /// <c>enode_cost</c>.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="cost">Computes each row's head cost; must return non-negative costs.</param>
        /// <returns>The new model.</returns>
        public CostModel WithNodeCost(FunctionId function, NodeCostFunction cost)
        {
            ArgumentNullException.ThrowIfNull(cost);

            var model = new CostModel(this);
            model.functionCosts.Remove(function);
            model.nodeCosts[function] = cost;
            return model;
        }

        /// <summary>
        /// Returns a model in which every value of the base sort <paramref name="sort"/> costs <paramref name="cost"/>.
        /// </summary>
        /// <param name="sort">A base sort.</param>
        /// <param name="cost">The cost; at least 0.</param>
        /// <returns>The new model.</returns>
        /// <exception cref="ArgumentException"><paramref name="sort"/> is not a base sort.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is negative.</exception>
        public CostModel WithBaseValueCost(Sort sort, long cost)
        {
            CheckKind(sort, SortKind.Base);
            ArgumentOutOfRangeException.ThrowIfNegative(cost);

            var model = new CostModel(this);
            model.baseCostFunctions.Remove(sort);
            model.baseCosts[sort] = cost;
            return model;
        }

        /// <summary>
        /// Returns a model in which the values of the base sort <paramref name="sort"/> cost what
        /// <paramref name="cost"/> says: egglog's <c>base_value_cost</c>.
        /// </summary>
        /// <param name="sort">A base sort.</param>
        /// <param name="cost">Computes each value's cost; must return non-negative costs.</param>
        /// <returns>The new model.</returns>
        /// <exception cref="ArgumentException"><paramref name="sort"/> is not a base sort.</exception>
        public CostModel WithBaseValueCost(Sort sort, BaseValueCostFunction cost)
        {
            CheckKind(sort, SortKind.Base);
            ArgumentNullException.ThrowIfNull(cost);

            var model = new CostModel(this);
            model.baseCosts.Remove(sort);
            model.baseCostFunctions[sort] = cost;
            return model;
        }

        /// <summary>
        /// Returns a model in which values of base sorts without an override cost <paramref name="cost"/>.
        /// </summary>
        /// <param name="cost">The cost; at least 0.</param>
        /// <returns>The new model.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is negative.</exception>
        public CostModel WithDefaultBaseValueCost(long cost)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(cost);

            var model = new CostModel(this);
            model.DefaultBaseValueCost = cost;
            return model;
        }

        /// <summary>
        /// Returns a model in which every container of the container sort <paramref name="sort"/> costs
        /// <paramref name="cost"/> plus its elements' costs.
        /// </summary>
        /// <param name="sort">A container sort.</param>
        /// <param name="cost">The container's own cost; at least 0.</param>
        /// <returns>The new model.</returns>
        /// <exception cref="ArgumentException"><paramref name="sort"/> is not a container sort.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is negative.</exception>
        public CostModel WithContainerCost(Sort sort, long cost)
        {
            CheckKind(sort, SortKind.Container);
            ArgumentOutOfRangeException.ThrowIfNegative(cost);

            var model = new CostModel(this);
            model.containerCostFunctions.Remove(sort);
            model.containerCosts[sort] = cost;
            return model;
        }

        /// <summary>
        /// Returns a model in which each container of the container sort <paramref name="sort"/> costs what
        /// <paramref name="cost"/> says plus its elements' costs.
        /// </summary>
        /// <param name="sort">A container sort.</param>
        /// <param name="cost">Computes each container's own cost; must return non-negative costs.</param>
        /// <returns>The new model.</returns>
        /// <exception cref="ArgumentException"><paramref name="sort"/> is not a container sort.</exception>
        public CostModel WithContainerCost(Sort sort, ContainerCostFunction cost)
        {
            CheckKind(sort, SortKind.Container);
            ArgumentNullException.ThrowIfNull(cost);

            var model = new CostModel(this);
            model.containerCosts.Remove(sort);
            model.containerCostFunctions[sort] = cost;
            return model;
        }

        /// <summary>
        /// Gets the constant head cost given for <paramref name="function"/>, if any.
        /// </summary>
        internal bool TryGetFunctionCost(FunctionId function, out long cost) => functionCosts.TryGetValue(function, out cost);

        /// <summary>
        /// Gets the per-row head cost given for <paramref name="function"/>, if any.
        /// </summary>
        internal NodeCostFunction? GetNodeCost(FunctionId function) => nodeCosts.TryGetValue(function, out var cost) ? cost : null;

        /// <summary>
        /// Gets how values of the base sort <paramref name="sort"/> cost: a constant, or a function when one is given.
        /// </summary>
        internal long GetBaseValueCost(Sort sort, out BaseValueCostFunction? function)
        {
            if (baseCostFunctions.TryGetValue(sort, out function))
                return 0;

            return baseCosts.TryGetValue(sort, out var cost) ? cost : DefaultBaseValueCost;
        }

        /// <summary>
        /// Gets how much containers of the container sort <paramref name="sort"/> cost on top of their elements: a
        /// constant, or a function when one is given.
        /// </summary>
        internal long GetContainerCost(Sort sort, out ContainerCostFunction? function)
        {
            if (containerCostFunctions.TryGetValue(sort, out function))
                return 0;

            return containerCosts.TryGetValue(sort, out var cost) ? cost : 0;
        }

        static void CheckKind(Sort sort, SortKind kind)
        {
            ArgumentNullException.ThrowIfNull(sort);
            if (sort.Kind != kind)
                throw new ArgumentException($"Sort {sort.Name} is a {sort.Kind} sort, not a {kind} sort.", nameof(sort));
        }

    }

}
