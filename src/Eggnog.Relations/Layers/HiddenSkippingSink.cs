using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IRowSink"/> that drops the rows a view hides before they reach <see cref="Inner"/>
    /// (colored-contexts spec §6.3): how a scan of an ancestor layer yields only what the view sees. Scans deliver rows
    /// in ascending order, so the test is a <see cref="HiddenCursor"/> walk; tombstones are already skipped by the scan.
    /// </summary>
    /// <typeparam name="TSink">The inner sink; a struct, so the scan is specialized for it.</typeparam>
    internal struct HiddenSkippingSink<TSink> : IRowSink
        where TSink : struct, IRowSink
    {

        HiddenCursor hidden;

        /// <summary>
        /// The sink that receives the rows the view sees. Read it back after the scan.
        /// </summary>
        public TSink Inner;

        /// <summary>
        /// Initializes a sink that forwards the rows <paramref name="hidden"/> does not hold to <paramref name="inner"/>.
        /// </summary>
        /// <param name="inner">The inner sink.</param>
        /// <param name="hidden">The rows the view hides.</param>
        public HiddenSkippingSink(TSink inner, HiddenRows hidden)
        {
            Inner = inner;
            this.hidden = hidden.Cursor();
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            if (hidden.IsHidden(rowId) == false)
                Inner.AddRow(rowId, row);
        }

    }

}
