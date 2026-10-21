namespace Eggnog
{

    /// <summary>
    /// The cost of one row of a constructor, without its children. Pure; called once per row extraction reads; it must not
    /// call into any graph, and such a call throws <see cref="System.InvalidOperationException"/>.
    /// </summary>
    /// <param name="node">The row.</param>
    /// <returns>The cost, at least 0.</returns>
    public delegate ulong NodeCostFunction(NodeView node);

}
