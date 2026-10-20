using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Builds extracted terms into a <see cref="TermDag"/>: egglog's <c>reconstruct_termdag_node_helper</c>
    /// (src/extract.rs:513-567), one hash-consed DAG term per extracted value.
    /// </summary>
    internal readonly struct TermDagBuilder : IExtractionBuilder<TermId>
    {

        readonly Extractor extractor;
        readonly TermDag dag;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="extractor">The extractor whose choices are built.</param>
        /// <param name="dag">The DAG to build into.</param>
        public TermDagBuilder(Extractor extractor, TermDag dag)
        {
            this.extractor = extractor;
            this.dag = dag;
        }

        /// <inheritdoc />
        public TermId Node(int sort, uint value, int node, long cost, ReadOnlySpan<TermId> children) => dag.App(extractor.TermNameOf(node), children);

        /// <inheritdoc />
        public TermId Base(int sort, uint value)
        {
            return extractor.SortAt(sort).ReconstructBase(extractor.EGraph.BaseValues, new Value(value), dag);
        }

        /// <inheritdoc />
        public TermId Container(int sort, uint value, int container, ReadOnlySpan<TermId> elements)
        {
            return ((ContainerSort)extractor.SortAt(sort)).Reconstruct(extractor.EGraph.ContainerValues, new Value(value), dag, elements);
        }

    }

}
