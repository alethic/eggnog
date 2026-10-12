namespace Eggnog.Relations
{

    /// <summary>
    /// What the layered executor reads of a context (colored-contexts spec §9.1, X21): its depth, its views and its find.
    /// Implemented by <see cref="ContextTables"/>, and by synthetic layer sets in executor tests.
    /// </summary>
    internal interface ILayerViewSource
    {

        /// <summary>
        /// Gets the context's depth: its views have this many layers plus one.
        /// </summary>
        int Depth { get; }

        /// <summary>
        /// Gets a counter that moves whenever a cached view is dropped because its structure changed; a rule set records
        /// it when it snapshots its views (X21).
        /// </summary>
        long ViewGeneration { get; }

        /// <summary>
        /// Gets the context's view of <paramref name="table"/>.
        /// </summary>
        /// <param name="table">A function table.</param>
        /// <returns>The view.</returns>
        TableView GetView(TableId table);

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/> in the context.
        /// </summary>
        /// <param name="id">Any id.</param>
        /// <returns>The canonical id.</returns>
        uint Find(uint id);

    }

}
