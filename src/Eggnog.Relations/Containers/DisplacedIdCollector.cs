using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Gathers the containers an incremental rebuild must revisit, straight from the scan of the union-find's recent
    /// rows: each displaced id itself (it may be a container's id) followed by the ids of the containers holding it.
    /// egglog's loop over the scanned buffer in <c>apply_rebuild_incremental</c>
    /// (core-relations/src/containers/mod.rs:479-496), fused with the scan so no intermediate buffer is built.
    /// </summary>
    /// <remarks>
    /// The scan projects the rebuilder's hint column, so each row is one cell. Keeps insertion order and drops repeats,
    /// as egglog's <c>IndexSet</c> does.
    /// </remarks>
    internal readonly struct DisplacedIdCollector : IRowSink
    {

        readonly ContainerValueIndex index;
        readonly OrderedValueSet output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="index">The environment's element index.</param>
        /// <param name="output">Receives the ids to revisit.</param>
        public DisplacedIdCollector(ContainerValueIndex index, OrderedValueSet output)
        {
            this.index = index;
            this.output = output;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            var displaced = row[0];
            output.Add(displaced);
            index.CollectContaining(displaced, output);
        }

    }

}
