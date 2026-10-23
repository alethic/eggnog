using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A container sort: its values are container ids (interned in the e-graph's <see cref="ContainerValues"/>, read
    /// through the view of the context extracted from, <see cref="ContainerView"/>) whose
    /// elements are values of inner sorts. egglog's <c>ContainerSort</c> (src/prelude.rs:853-950). Extraction costs a
    /// container as its own cost plus the costs of its elements, and rebuilds it from its elements' terms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal, as the container machinery it reads is: the frontend's vectors, sets, maps and partial functions derive
    /// from it, one subclass per container type. Implementations must be immutable and thread-safe.
    /// </para>
    /// <para>
    /// Extraction calls <see cref="AppendInnerValues"/> once per distinct container value it meets and
    /// <see cref="Reconstruct"/> once per container term it builds, both on cold paths.
    /// </para>
    /// </remarks>
    internal abstract class ContainerSort : Sort
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        protected ContainerSort(string name)
            : base(name, SortKind.Container)
        {

        }

        /// <summary>
        /// Gets the sorts of the elements, one level deep: egglog's <c>inner_sorts</c>. Extraction's sort reachability
        /// walks through them.
        /// </summary>
        public abstract IReadOnlyList<Sort> InnerSorts { get; }

        /// <summary>
        /// Appends the elements of the container <paramref name="value"/>, each with its sort, in the container's order:
        /// egglog's <c>inner_values</c>. Every sort appended must be one of <see cref="InnerSorts"/>.
        /// </summary>
        /// <param name="containers">The containers of the context extracted from (<see cref="ContainerView"/>).</param>
        /// <param name="value">The container's id.</param>
        /// <param name="destination">Receives the elements.</param>
        /// <exception cref="InvalidOperationException">No container of this sort has the id.</exception>
        public abstract void AppendInnerValues(ContainerView containers, Value value, List<SortedValue> destination);

        /// <summary>
        /// Adds the container <paramref name="value"/> to <paramref name="dag"/>, given the terms of its elements in the
        /// order <see cref="AppendInnerValues"/> lists them: egglog's <c>reconstruct_termdag_container</c>
        /// (<c>(vec-of e...)</c>, <c>(set-of e...)</c>, nested <c>(map-insert ...)</c>, and so on).
        /// </summary>
        /// <param name="containers">The containers of the context extracted from (<see cref="ContainerView"/>).</param>
        /// <param name="value">The container's id.</param>
        /// <param name="dag">The term DAG.</param>
        /// <param name="elements">The terms of the elements.</param>
        /// <returns>The container's term.</returns>
        public abstract TermId Reconstruct(ContainerView containers, Value value, TermDag dag, ReadOnlySpan<TermId> elements);

        /// <inheritdoc />
        internal override bool FitsColumn(EGraph egraph, ColumnTy type) => type.IsId;

    }

}
