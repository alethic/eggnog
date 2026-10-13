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

        public TestVecSort(string name, Sort element)
            : base(name)
        {
            Element = element;
            inner = [element];
        }

        public Sort Element { get; }

        public override IReadOnlyList<Sort> InnerSorts => inner;

        public override void AppendInnerValues(ContainerValues containers, Value value, List<SortedValue> destination)
        {
            if (containers.TryGetValue<VecContainer>(value.Rep, out var vec) == false)
                throw new InvalidOperationException($"No vector has id {value}.");

            foreach (var element in vec.Values)
                destination.Add(new SortedValue(Element, new Value(element)));
        }

        public override TermId Reconstruct(ContainerValues containers, Value value, TermDag dag, ReadOnlySpan<TermId> elements)
        {
            return elements.IsEmpty ? dag.App("vec-empty") : dag.App("vec-of", elements);
        }

    }

}
