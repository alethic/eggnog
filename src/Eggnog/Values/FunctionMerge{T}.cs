using System;

namespace Eggnog
{

    /// <summary>
    /// egglog's <c>:merge</c> of a function that is not a lattice: <c>new</c>, <c>old</c>, or any function of the old
    /// and the new value (<c>(+ old new)</c>), made by <see cref="FunctionMerge"/>. A function defined with one
    /// (<see cref="EGraph.DefineFunction{TOut}(string, FunctionMerge{TOut}, TableOptions)"/>) takes a second value for a
    /// key by merging it with the first, as egglog's does. Such a merge depends on the order of writes, so only the root
    /// writes the function. Below the root (in a child scope or a case) a host write of it is refused, and so are a host
    /// merge of classes that would rewrite one of its rows and a rule that would write it; a rule's union there that
    /// would rewrite one of its rows fails the run. No rule reads it through a scoped view, whose mirror would be
    /// written below the root.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    public readonly struct FunctionMerge<T>
    {

        readonly FunctionMergeDefinition? definition;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="definition">The merge, of values of <typeparamref name="T"/>.</param>
        internal FunctionMerge(FunctionMergeDefinition definition)
        {
            this.definition = definition;
        }

        /// <summary>
        /// Gets the merge's name: <c>new</c>, <c>old</c>, the name given to <see cref="FunctionMerge.Of{T}"/>, or the
        /// rendering of an expression (<see cref="FunctionMerge.Expression{T}"/>), such as <c>(Join old new)</c>.
        /// </summary>
        public string Name => definition?.Name ?? string.Empty;

        /// <summary>
        /// Gets the merge's definition.
        /// </summary>
        /// <exception cref="ArgumentException">The merge is the default value, which <see cref="FunctionMerge"/> never
        /// makes.</exception>
        internal FunctionMergeDefinition Definition =>
            definition ?? throw new ArgumentException("A default FunctionMerge merges nothing; make one with FunctionMerge.New, Old, Of or Expression.");

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;

    }

}
