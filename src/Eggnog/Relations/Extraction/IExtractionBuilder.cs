using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Builds the representation of extracted terms bottom-up as <see cref="Extractor"/> walks the chosen nodes: one call
    /// per distinct value, children first, and one more <see cref="Node"/> call for each variant of a class. Implemented by structs (<see cref="TermDagBuilder"/>,
    /// <see cref="ExtractedTermBuilder"/>), so the walk is specialized for each representation.
    /// </summary>
    /// <typeparam name="TResult">What a built term is.</typeparam>
    internal interface IExtractionBuilder<TResult>
    {

        /// <summary>
        /// Builds the term of an e-class from its chosen node, or from one of its variants: egglog's <c>termdag.app</c>
        /// over the children's terms.
        /// </summary>
        /// <param name="sort">The class's sort index.</param>
        /// <param name="value">The class's canonical id.</param>
        /// <param name="node">The class's chosen node, or the variant's.</param>
        /// <param name="cost">The term's cost: the class's for its chosen node, the node's own for a variant.</param>
        /// <param name="children">The terms of the node's child columns, in order.</param>
        /// <returns>The term.</returns>
        TResult Node(int sort, uint value, int node, ulong cost, ReadOnlySpan<TResult> children);

        /// <summary>
        /// Builds the term of a base value: egglog's <c>reconstruct_termdag_base</c>.
        /// </summary>
        /// <param name="sort">The value's sort index.</param>
        /// <param name="value">The value.</param>
        /// <returns>The term.</returns>
        TResult Base(int sort, uint value);

        /// <summary>
        /// Builds the term of a container from its elements' terms: egglog's <c>reconstruct_termdag_container</c>.
        /// </summary>
        /// <param name="sort">The container's sort index.</param>
        /// <param name="value">The container's id.</param>
        /// <param name="container">The container's index in the extractor.</param>
        /// <param name="elements">The terms of the elements, in order.</param>
        /// <returns>The term.</returns>
        TResult Container(int sort, uint value, int container, ReadOnlySpan<TResult> elements);

    }

}
