using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Builds extracted terms as <see cref="ExtractedTerm"/> objects, each carrying its sort, value and cost.
    /// </summary>
    internal readonly struct ExtractedTermBuilder : IExtractionBuilder<ExtractedTerm>
    {

        readonly Extractor extractor;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="extractor">The extractor whose choices are built.</param>
        public ExtractedTermBuilder(Extractor extractor)
        {
            this.extractor = extractor;
        }

        /// <inheritdoc />
        public ExtractedTerm Node(int sort, uint value, int node, ulong cost, ReadOnlySpan<ExtractedTerm> children)
        {
            return new ExtractedTerm(
                extractor.EGraph,
                ExtractedTermKind.Node,
                extractor.SortAt(sort),
                new Value(value),
                extractor.FunctionOf(node),
                extractor.TermNameOf(node),
                cost,
                children.ToArray());
        }

        /// <inheritdoc />
        public ExtractedTerm Base(int sort, uint value)
        {
            var s = extractor.SortAt(sort);
            var text = s.FormatValue(extractor.EGraph.BaseValues, new Value(value));
            var cost = extractor.BaseCost(sort, value);
            var leaf = Array.Empty<ExtractedTerm>();
            return new ExtractedTerm(extractor.EGraph, ExtractedTermKind.Base, s, new Value(value), default, text, cost, leaf);
        }

        /// <inheritdoc />
        public ExtractedTerm Container(int sort, uint value, int container, ReadOnlySpan<ExtractedTerm> elements)
        {
            var s = extractor.SortAt(sort);
            extractor.TryGetContainerCost(container, out var cost);
            var children = elements.ToArray();
            return new ExtractedTerm(extractor.EGraph, ExtractedTermKind.Container, s, new Value(value), default, s.Name, cost, children);
        }

    }

}
