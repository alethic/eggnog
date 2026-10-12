namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The store and child of <see cref="LayeredParallelRunTests"/>: a frozen root with edges, a constructor row per id,
    /// and two tables only the child's rules write.
    /// </summary>
    /// <param name="Store">The store.</param>
    /// <param name="Edges">The edge relation.</param>
    /// <param name="Cons">The constructor, a row per id.</param>
    /// <param name="Out">A relation the child's rules write.</param>
    /// <param name="Best">A max-function the child's rules write.</param>
    /// <param name="Child">The child the rules run in.</param>
    /// <param name="Ids">The ids the edges connect.</param>
    sealed record LayeredRunWorld(Store Store, TableId Edges, TableId Cons, TableId Out, TableId Best, Context Child, uint[] Ids);

}
