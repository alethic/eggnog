using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Adds the physical rows an index probe yields to an <see cref="Extractor"/>'s graph, skipping subsumed rows, each
    /// placed by its row id above the layer's base: the row sink of on-demand discovery, which finds a class's nodes
    /// through the cached index on its function's output column. The table's scan has already dropped deleted rows and,
    /// in a child context, the rows its view hides.
    /// </summary>
    internal readonly struct ExtractionRowSink : IRowSink
    {

        readonly Extractor extractor;
        readonly int slot;
        readonly int functionColumns;
        readonly int subsumeColumn;
        readonly long layerBase;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="extractor">The extractor.</param>
        /// <param name="slot">The function's slot in the extractor.</param>
        /// <param name="math">The layout of the function's table.</param>
        /// <param name="layerBase">The position of the table's first row: its layer's index in a child context's view,
        /// shifted above the row ids; 0 at the root.</param>
        public ExtractionRowSink(Extractor extractor, int slot, SchemaMath math, long layerBase)
        {
            this.extractor = extractor;
            this.slot = slot;
            this.layerBase = layerBase;
            functionColumns = math.FunctionColumns;
            subsumeColumn = math.SubsumeColumnOrNone;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            if (subsumeColumn >= 0 && row[subsumeColumn] == SchemaMath.Subsumed)
                return;

            extractor.AddNode(slot, layerBase + (uint)rowId, row.Slice(0, functionColumns));
        }

    }

}
