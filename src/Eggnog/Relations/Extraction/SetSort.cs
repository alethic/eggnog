using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A set sort over <see cref="SetContainer"/>: egglog's <c>SetSort</c> as extraction sees it (src/sort/set.rs:26-147),
    /// whose elements are its values in cell order and whose term is <c>(set-of e...)</c>. Immutable and thread-safe.
    /// </summary>
    internal sealed class SetSort : ContainerSort
    {

        readonly Sort[] inner;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <param name="element">The sort of the elements.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        public SetSort(string name, Sort element)
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
        public override void AppendInnerValues(ContainerValues containers, Value value, List<SortedValue> destination)
        {
            if (containers.TryGetValue<SetContainer>(value.Rep, out var set) == false)
                throw new InvalidOperationException($"No set of sort {Name} has id {value}.");

            foreach (var element in set.Elements)
                destination.Add(new SortedValue(Element, new Value(element)));
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>reconstruct_termdag</c> (set.rs:135-143): <c>set-of</c>, even when empty.</remarks>
        public override TermId Reconstruct(ContainerValues containers, Value value, TermDag dag, ReadOnlySpan<TermId> elements)
        {
            return dag.App("set-of", elements);
        }

    }

}
