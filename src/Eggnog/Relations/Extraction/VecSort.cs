using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A vector sort over <see cref="VecContainer"/>: egglog's <c>VecSort</c> as extraction sees it (src/sort/vec.rs:91-191),
    /// whose elements are its values in order and whose term is <c>(vec-empty)</c> or <c>(vec-of e...)</c>. Immutable and
    /// thread-safe.
    /// </summary>
    internal sealed class VecSort : ContainerSort
    {

        readonly Sort[] inner;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <param name="element">The sort of the elements.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        public VecSort(string name, Sort element)
            : base(name)
        {
            ArgumentNullException.ThrowIfNull(element);
            Element = element;
            inner = [element];
        }

        /// <summary>
        /// Gets the sort of the elements.
        /// </summary>
        public Sort Element { get; }

        /// <inheritdoc />
        public override IReadOnlyList<Sort> InnerSorts => inner;

        /// <inheritdoc />
        public override void AppendInnerValues(ContainerView containers, Value value, List<SortedValue> destination)
        {
            if (containers.TryGetContainer<VecContainer>(value.Rep, out var vec) == false)
                throw new InvalidOperationException($"No vector of sort {Name} has id {value}.");

            foreach (var element in vec.Values)
                destination.Add(new SortedValue(Element, new Value(element)));
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>reconstruct_termdag</c> (vec.rs:175-187).</remarks>
        public override TermId Reconstruct(ContainerView containers, Value value, TermDag dag, ReadOnlySpan<TermId> elements)
        {
            return elements.IsEmpty ? dag.App("vec-empty") : dag.App("vec-of", elements);
        }

    }

}
