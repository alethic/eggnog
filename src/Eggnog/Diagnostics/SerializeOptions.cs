using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// What <see cref="Scope.Serialize"/> includes: egglog's <c>SerializeConfig</c>. The default includes every table
    /// and every row, as egglog's does for its JSON export.
    /// </summary>
    public sealed class SerializeOptions
    {

        /// <summary>
        /// Gets the most tables with rows to include (egglog's <c>max_functions</c>); the tables after them are
        /// discarded (<see cref="SerializedGraph.DiscardedTables"/>). <c>null</c> for no limit.
        /// </summary>
        public int? MaxTables { get; init; }

        /// <summary>
        /// Gets the most rows to include of each table (egglog's <c>max_calls_per_function</c>); a table with more is
        /// truncated (<see cref="SerializedGraph.TruncatedTables"/>). <c>null</c> for no limit.
        /// </summary>
        public int? MaxRowsPerTable { get; init; }

        /// <summary>
        /// Gets the classes to mark as roots (egglog's <c>root_eclasses</c>), each from the serialized scope or an
        /// ancestor, as a sort value's <see cref="ISort{TSort}.Id"/>.
        /// </summary>
        public IReadOnlyList<ClassId> Roots { get; init; } = Array.Empty<ClassId>();

    }

}
