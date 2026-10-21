using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A multiset sort over <see cref="MultiSetContainer"/>: egglog's <c>MultiSetSort</c> as extraction sees it
    /// (src/sort/multiset.rs:100-224), whose elements are its values with their repeats, in cell order, and whose term is
    /// <c>(multiset-of e...)</c>. Immutable and thread-safe.
    /// </summary>
    internal sealed class MultiSetSort : ContainerSort
    {

        readonly Sort[] inner;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <param name="element">The sort of the elements.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        public MultiSetSort(string name, Sort element)
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
        /// <remarks>egglog's <c>inner_values</c> (multiset.rs:114-126): each element repeated by its count.</remarks>
        public override void AppendInnerValues(ContainerValues containers, Value value, List<SortedValue> destination)
        {
            if (containers.TryGetValue<MultiSetContainer>(value.Rep, out var multiset) == false)
                throw new InvalidOperationException($"No multiset of sort {Name} has id {value}.");

            for (var i = 0; i < multiset.Elements.Length; i++)
                for (var k = 0L; k < multiset.Counts[i]; k++)
                    destination.Add(new SortedValue(Element, new Value(multiset.Elements[i])));
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>reconstruct_termdag</c> (multiset.rs:211-219).</remarks>
        public override TermId Reconstruct(ContainerValues containers, Value value, TermDag dag, ReadOnlySpan<TermId> elements)
        {
            return dag.App("multiset-of", elements);
        }

    }

}
