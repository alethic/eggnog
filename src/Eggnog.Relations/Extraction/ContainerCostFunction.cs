namespace Eggnog.Relations
{

    /// <summary>
    /// Computes a container's own cost, which extraction adds to the sum of its elements' costs: the additive form of
    /// egglog's <c>CostModel::container_cost</c> (src/extract.rs:23-39), whose default is the sum alone. Must return a
    /// non-negative cost; called once per distinct container extraction reads.
    /// </summary>
    /// <param name="sort">The container's sort.</param>
    /// <param name="value">The container's id.</param>
    /// <returns>The container's own cost.</returns>
    internal delegate long ContainerCostFunction(Sort sort, Value value);

}
