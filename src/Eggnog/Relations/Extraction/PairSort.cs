using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A pair sort over <see cref="PairContainer"/>: egglog's <c>PairSort</c> as extraction sees it (src/sort/pair.rs:36-148),
    /// whose elements are its two values and whose term is <c>(pair a b)</c>. Immutable and thread-safe.
    /// </summary>
    internal sealed class PairSort : ContainerSort
    {

        readonly Sort[] inner;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <param name="first">The sort of the first element.</param>
        /// <param name="second">The sort of the second element.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        public PairSort(string name, Sort first, Sort second)
            : base(name)
        {
            ArgumentNullException.ThrowIfNull(first);
            ArgumentNullException.ThrowIfNull(second);
            First = first;
            Second = second;
            inner = [first, second];
        }

        /// <summary>
        /// Gets the sort of the first element.
        /// </summary>
        public Sort First { get; }

        /// <summary>
        /// Gets the sort of the second element.
        /// </summary>
        public Sort Second { get; }

        /// <inheritdoc />
        public override IReadOnlyList<Sort> InnerSorts => inner;

        /// <inheritdoc />
        public override void AppendInnerValues(ContainerView containers, Value value, List<SortedValue> destination)
        {
            if (containers.TryGetContainer<PairContainer>(value.Rep, out var pair) == false)
                throw new InvalidOperationException($"No pair of sort {Name} has id {value}.");

            destination.Add(new SortedValue(First, new Value(pair.First)));
            destination.Add(new SortedValue(Second, new Value(pair.Second)));
        }

        /// <inheritdoc />
        public override TermId Reconstruct(ContainerView containers, Value value, TermDag dag, ReadOnlySpan<TermId> elements)
        {
            if (elements.Length != 2)
                throw new ArgumentException($"A pair has two elements, not {elements.Length}.", nameof(elements));

            return dag.App("pair", elements);
        }

    }

}
