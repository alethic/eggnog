using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Stages the row <see cref="Values"/> into <see cref="Table"/> in every live lane. egglog's <c>Instr::Insert</c>
    /// (core-relations/src/action/mod.rs:929-934, 774-780): <c>set</c> and <c>union</c> in the bridge.
    /// </summary>
    internal sealed class InsertInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="values">The row; one operand per column.</param>
        public InsertInstr(TableId table, ReadOnlySpan<QueryEntry> values)
            : base(InstrKind.Insert)
        {
            Table = table;
            Values = CopyOperands(values, nameof(values));
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public TableId Table { get; }

        /// <summary>
        /// Gets the row.
        /// </summary>
        public QueryEntry[] Values { get; }

        /// <inheritdoc />
        public override string ToString() => AppendList(new StringBuilder("Insert ").Append(Table).Append(' '), Values).ToString();

    }

}
