using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The row sink behind <see cref="EGraph.ForEach{TVisitor}"/>: turns each physical row of a function's table into a
    /// <see cref="FunctionRow"/> (the schema columns, and whether the subsumption cell is set) and hands it to the visitor
    /// until the visitor returns <c>false</c>. The body of egglog-bridge's <c>drain_buf!</c> (egglog-bridge/src/lib.rs:453-467),
    /// without the intermediate buffer: the rows come straight from the table's storage.
    /// </summary>
    /// <typeparam name="TVisitor">The visitor type; a struct, so the scan is specialized for it.</typeparam>
    internal struct FunctionRowSink<TVisitor> : IRowSink
        where TVisitor : struct, IFunctionRowVisitor
    {

        readonly int functionColumns;
        readonly int subsumeColumn;

        /// <summary>
        /// The visitor; read it back after the scan to observe its state.
        /// </summary>
        public TVisitor Visitor;

        /// <summary>
        /// Whether the visitor asked to stop; later rows are ignored.
        /// </summary>
        public bool Stopped;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="visitor">The visitor.</param>
        /// <param name="math">The layout of the function's table.</param>
        public FunctionRowSink(TVisitor visitor, SchemaMath math)
        {
            Visitor = visitor;
            functionColumns = math.FunctionColumns;
            subsumeColumn = math.SubsumeColumnOrNone;
            Stopped = false;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            if (Stopped)
                return;

            var subsumed = subsumeColumn >= 0 && row[subsumeColumn] == SchemaMath.Subsumed;
            var values = MemoryMarshal.Cast<uint, Value>(row.Slice(0, functionColumns));
            if (Visitor.Visit(new FunctionRow(values, subsumed)) == false)
                Stopped = true;
        }

    }

}
