using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A map sort over <see cref="MapContainer"/>: egglog's <c>MapSort</c> as extraction sees it (src/sort/map.rs:40-176),
    /// whose elements are its keys and values, <c>k0 v0 k1 v1 …</c> in key-cell order, and whose term is a chain of
    /// <c>map-insert</c>s over <c>(map-empty)</c>. Immutable and thread-safe.
    /// </summary>
    internal sealed class MapSort : ContainerSort
    {

        readonly Sort[] inner;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The sort's name.</param>
        /// <param name="key">The sort of the keys.</param>
        /// <param name="value">The sort of the values.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        public MapSort(string name, Sort key, Sort value)
            : base(name)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(value);
            Key = key;
            Value = value;
            inner = [key, value];
        }

        /// <summary>
        /// Gets the sort of the keys.
        /// </summary>
        public Sort Key { get; }

        /// <summary>
        /// Gets the sort of the values.
        /// </summary>
        public Sort Value { get; }

        /// <inheritdoc />
        public override IReadOnlyList<Sort> InnerSorts => inner;

        /// <inheritdoc />
        /// <remarks>egglog's <c>inner_values</c> (map.rs:124-137).</remarks>
        public override void AppendInnerValues(ContainerValues containers, Value value, List<SortedValue> destination)
        {
            if (containers.TryGetValue<MapContainer>(value.Rep, out var map) == false)
                throw new InvalidOperationException($"No map of sort {Name} has id {value}.");

            for (var i = 0; i < map.Count; i++)
            {
                destination.Add(new SortedValue(Key, new Value(map.Keys[i])));
                destination.Add(new SortedValue(Value, new Value(map.Values[i])));
            }
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>reconstruct_termdag</c> (map.rs:157-171): <c>(map-insert … (map-empty) k v)</c>.</remarks>
        public override TermId Reconstruct(ContainerValues containers, Value value, TermDag dag, ReadOnlySpan<TermId> elements)
        {
            if (elements.Length % 2 != 0)
                throw new ArgumentException($"A map's elements come in pairs, not {elements.Length}.", nameof(elements));

            var term = dag.App("map-empty");
            for (var i = 0; i < elements.Length; i += 2)
                term = dag.App("map-insert", [term, elements[i], elements[i + 1]]);

            return term;
        }

    }

}
