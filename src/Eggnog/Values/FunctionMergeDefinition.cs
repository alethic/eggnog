namespace Eggnog
{

    /// <summary>
    /// A function's merge that is not a lattice (<see cref="FunctionMerge{T}"/>), as its table and the host batch use it.
    /// </summary>
    internal abstract class FunctionMergeDefinition
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The merge's name.</param>
        private protected FunctionMergeDefinition(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Gets the merge's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets whether the merge calls a function of the old and the new value (<see cref="FunctionMerge.Of{T}"/>), which
        /// decodes values; <c>false</c> for <c>new</c> and <c>old</c>, which merge classes too.
        /// </summary>
        public abstract bool CallsFunction { get; }

        /// <summary>
        /// Creates the engine merge of a function with this merge, registering its external function if it has one: not a
        /// lattice merge, so that only the root writes the function.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="functionName">The function's name, for messages.</param>
        /// <param name="type">The function's output type.</param>
        /// <returns>The engine merge.</returns>
        public abstract Relations.MergeFn CreateMergeFn(EGraph graph, string functionName, ColumnType type);

        /// <summary>
        /// Merges two cells of the function's output, as the engine's merge will, interning a new value.
        /// </summary>
        /// <param name="type">The function's output type.</param>
        /// <param name="stored">The old cell.</param>
        /// <param name="incoming">The new cell.</param>
        /// <returns>The merged cell.</returns>
        public abstract uint MergeCells(ColumnType type, uint stored, uint incoming);

    }

}
