namespace Eggnog.Relations
{

    /// <summary>
    /// Computes the cost of a base value: egglog's <c>CostModel::base_value_cost</c> (src/extract.rs:41-49). Must return
    /// a non-negative cost; called once per base value occurrence extraction reads.
    /// </summary>
    /// <param name="sort">The value's sort.</param>
    /// <param name="value">The value.</param>
    /// <returns>The value's cost.</returns>
    internal delegate long BaseValueCostFunction(Sort sort, Value value);

}
