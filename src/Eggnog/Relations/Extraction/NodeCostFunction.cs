namespace Eggnog.Relations
{

    /// <summary>
    /// Computes the cost of one row of a function, without the costs of its children: the per-row form of egglog's
    /// <c>CostModel::enode_cost</c> (src/extract.rs:20-21). Must not change the
    /// e-graph; it is called once per row extraction reads.
    /// </summary>
    /// <param name="function">The function.</param>
    /// <param name="row">The row: keys and output, valid only during the call.</param>
    /// <returns>The row's own cost.</returns>
    internal delegate ulong NodeCostFunction(FunctionId function, FunctionRow row);

}
