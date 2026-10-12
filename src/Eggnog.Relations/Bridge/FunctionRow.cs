using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A row of a function as <see cref="EGraph.ForEach{TVisitor}"/> presents it: the key and output columns, without the
    /// timestamp and subsumption bookkeeping, and whether the row is subsumed. egglog-bridge's <c>FunctionRow</c>
    /// (egglog-bridge/src/lib.rs:1565-1570).
    /// </summary>
    /// <remarks>
    /// The values alias the table's storage and are valid only during the visit that received them; copy them to keep
    /// them.
    /// </remarks>
    internal readonly ref struct FunctionRow
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="values">The key and output columns.</param>
        /// <param name="subsumed">Whether the row is subsumed.</param>
        public FunctionRow(ReadOnlySpan<Value> values, bool subsumed)
        {
            Values = values;
            Subsumed = subsumed;
        }

        /// <summary>
        /// Gets the key columns followed by the output column.
        /// </summary>
        public ReadOnlySpan<Value> Values { get; }

        /// <summary>
        /// Gets whether the row is subsumed. Always <c>false</c> for a function that cannot subsume.
        /// </summary>
        public bool Subsumed { get; }

    }

}
