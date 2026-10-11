using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Adds every row of a function that is not subsumed to an <see cref="Extractor"/>'s graph: the scan egglog's
    /// <c>bellman_ford</c> relaxes over (<c>backend.for_each</c> with <c>if !row.subsumed</c>, src/extract.rs:420-457),
    /// done once here, since Knuth's algorithm needs each row only once.
    /// </summary>
    internal readonly struct ExtractionScanVisitor : IFunctionRowVisitor
    {

        readonly Extractor extractor;
        readonly int slot;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="extractor">The extractor.</param>
        /// <param name="slot">The function's slot in the extractor.</param>
        public ExtractionScanVisitor(Extractor extractor, int slot)
        {
            this.extractor = extractor;
            this.slot = slot;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Visit(FunctionRow row)
        {
            if (row.Subsumed == false)
                extractor.AddNode(slot, MemoryMarshal.Cast<Value, uint>(row.Values));

            return true;
        }

    }

}
