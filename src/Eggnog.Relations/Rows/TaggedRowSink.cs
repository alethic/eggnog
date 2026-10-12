using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IRowSink"/> struct that appends to a <see cref="TaggedRowBuffer"/>. Pass it to scans generic over
    /// <c>TSink : struct, IRowSink</c>: the JIT specializes the scan for this struct and inlines
    /// <see cref="TaggedRowBuffer.AddRow(int, ReadOnlySpan{uint})"/> into the per-row loop, where egglog makes a
    /// virtual call through <c>&amp;mut dyn RowSink</c>.
    /// </summary>
    internal readonly struct TaggedRowSink : IRowSink
    {

        readonly TaggedRowBuffer buffer;

        /// <summary>
        /// Initializes a sink that appends to <paramref name="buffer"/>.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        public TaggedRowSink(TaggedRowBuffer buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            this.buffer = buffer;
        }

        /// <summary>
        /// Gets the buffer the sink appends to.
        /// </summary>
        public TaggedRowBuffer Buffer => buffer;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            buffer.AddRow(rowId, row);
        }

    }

}
