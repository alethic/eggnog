using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A map sort over the test <see cref="MapContainer"/>: egglog's <c>MapSort</c> as extraction sees it
    /// (src/sort/map.rs:113-169). Its elements are the keys and values interleaved, and it reconstructs as egglog does:
    /// <c>(map-insert ... (map-insert (map-empty) k v) ...)</c>.
    /// </summary>
    sealed class TestMapSort : ContainerSort
    {

        readonly Sort[] inner;

        /// <summary>
        /// Initializes a map sort named <paramref name="name"/> from <paramref name="key"/> to
        /// <paramref name="value"/>.
        /// </summary>
        public TestMapSort(string name, Sort key, Sort value)
            : base(name)
        {
            KeySort = key;
            ValueSort = value;
            inner = [key, value];
        }

        /// <summary>
        /// Gets the sort of the keys.
        /// </summary>
        public Sort KeySort { get; }

        /// <summary>
        /// Gets the sort of the values.
        /// </summary>
        public Sort ValueSort { get; }

        /// <inheritdoc />
        public override IReadOnlyList<Sort> InnerSorts => inner;

        /// <inheritdoc />
        public override void AppendInnerValues(ContainerValues containers, Value value, List<SortedValue> destination)
        {
            if (containers.TryGetValue<MapContainer>(value.Rep, out var map) == false)
                throw new InvalidOperationException($"No map has id {value}.");

            for (var i = 0; i < map.Keys.Length; i++)
            {
                destination.Add(new SortedValue(KeySort, new Value(map.Keys[i])));
                destination.Add(new SortedValue(ValueSort, new Value(map.Values[i])));
            }
        }

        /// <inheritdoc />
        public override TermId Reconstruct(ContainerValues containers, Value value, TermDag dag, ReadOnlySpan<TermId> elements)
        {
            var term = dag.App("map-empty");
            for (var i = 0; i + 1 < elements.Length; i += 2)
                term = dag.App("map-insert", term, elements[i], elements[i + 1]);

            return term;
        }

    }

}
