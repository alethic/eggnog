using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The declaration of a function added to an <see cref="EGraph"/>: egglog-bridge's <c>FunctionConfig</c>
    /// (egglog-bridge/src/lib.rs:191-203).
    /// </summary>
    /// <remarks>
    /// The frontend maps a constructor to <see cref="DefaultVal.FreshId"/>, <see cref="MergeFn.UnionId"/> and
    /// <see cref="CanSubsume"/>; a custom function to <see cref="DefaultVal.Fail"/> with <see cref="MergeFn.AssertEq"/>
    /// or its <c>:merge</c> expression; a relation to a constructor whose output is a fresh, non-unionable sort
    /// (map §3.1). <see cref="EGraph.AddTable"/> copies what it needs, so the instance may be reused.
    /// </remarks>
    internal sealed class FunctionConfig
    {

        /// <summary>
        /// Gets the type of every column, keys first; the last column is the output. At least one column.
        /// </summary>
        public required IReadOnlyList<ColumnTy> Schema { get; init; }

        /// <summary>
        /// Gets what a lookup of a missing key does.
        /// </summary>
        public required DefaultVal Default { get; init; }

        /// <summary>
        /// Gets how conflicting outputs for one key are merged.
        /// </summary>
        public required MergeFn Merge { get; init; }

        /// <summary>
        /// Gets the function's name, under which its <see cref="TableAction"/> is registered.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets whether rows of the function can be subsumed, which adds a subsumption column to its table.
        /// </summary>
        public bool CanSubsume { get; init; }

    }

}
