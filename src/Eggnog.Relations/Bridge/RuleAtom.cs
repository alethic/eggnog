using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A table atom of a rule built with an <see cref="EGraphRuleBuilder"/>: one entry per physical column of the table,
    /// the function's columns followed by the atom's own timestamp variable and, for a table that can subsume, its
    /// subsumption entry. One element of egglog-bridge's <c>Query::atoms</c> (egglog-bridge/src/rule.rs:107).
    /// </summary>
    /// <remarks>
    /// Because every atom carries a fresh timestamp variable, no atom of an e-graph rule is ever free of variables (map
    /// §3.3), and the semi-naive variants constrain exactly that column (<see cref="SchemaMath.TimestampColumn"/>).
    /// Immutable; the entry array is owned by the atom.
    /// </remarks>
    internal sealed class RuleAtom
    {

        readonly RuleEntry[] entries;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table the atom reads.</param>
        /// <param name="entries">One entry per physical column; owned by the new instance.</param>
        /// <param name="math">The layout of the table's columns.</param>
        public RuleAtom(TableId table, RuleEntry[] entries, SchemaMath math)
        {
            ArgumentNullException.ThrowIfNull(entries);
            Table = table;
            this.entries = entries;
            Math = math;
        }

        /// <summary>
        /// Gets the table the atom reads.
        /// </summary>
        public TableId Table { get; }

        /// <summary>
        /// Gets the entries, one per physical column.
        /// </summary>
        public ReadOnlySpan<RuleEntry> Entries => entries;

        /// <summary>
        /// Gets the layout of the table's columns; its timestamp column is the one semi-naive evaluation constrains.
        /// </summary>
        public SchemaMath Math { get; }

        /// <inheritdoc />
        public override string ToString() => $"{Table}({string.Join(", ", entries)})";

    }

}
