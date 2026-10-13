using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A vector sort over the test <see cref="VecContainer"/>: egglog's <c>VecSort</c> as extraction sees it
    /// (src/sort/vec.rs:91-189), reconstructing as <c>(vec-empty)</c> or <c>(vec-of e...)</c>.
    /// </summary>
    sealed class TestVecSort : ContainerSort
    {

        readonly Sort[] inner;

        /// <summary>
        /// Initializes a vector sort named <paramref name="name"/> over <paramref name="element"/>.
        /// </summary>
        public TestVecSort(string name, Sort element)
            : base(name)
        {
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
        public override void AppendInnerValues(ContainerValues containers, Value value, List<SortedValue> destination)
        {
            if (containers.TryGetValue<VecContainer>(value.Rep, out var vec) == false)
                throw new InvalidOperationException($"No vector has id {value}.");

            foreach (var element in vec.Values)
                destination.Add(new SortedValue(Element, new Value(element)));
        }

        /// <inheritdoc />
        public override TermId Reconstruct(ContainerValues containers, Value value, TermDag dag, ReadOnlySpan<TermId> elements)
        {
            return elements.IsEmpty ? dag.App("vec-empty") : dag.App("vec-of", elements);
        }

    }

}
