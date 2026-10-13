using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks <see cref="Args"/> up in <see cref="Table"/> and binds <see cref="Column"/> of the row to
    /// <see cref="Destination"/>; a lane whose key is missing halts. egglog's <c>Instr::Lookup</c>
    /// (core-relations/src/action/mod.rs:907-914, 727-735).
    /// </summary>
    internal sealed class LookupInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="args">The key; one operand per key column.</param>
        /// <param name="column">The column of the row to bind.</param>
        /// <param name="destination">The variable defined.</param>
        public LookupInstr(TableId table, ReadOnlySpan<QueryEntry> args, ColumnId column, Variable destination)
            : base(InstrKind.Lookup)
        {
            Table = table;
            Args = CopyOperands(args, nameof(args));
            Column = CheckColumn(column, nameof(column));
            Destination = CheckDestination(destination, nameof(destination));
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public TableId Table { get; }

        /// <summary>
        /// Gets the key.
        /// </summary>
        public QueryEntry[] Args { get; }

        /// <summary>
        /// Gets the column bound to <see cref="Destination"/>.
        /// </summary>
        public ColumnId Column { get; }

        /// <summary>
        /// Gets the variable defined.
        /// </summary>
        public Variable Destination { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(Destination).Append(" <- Lookup ").Append(Table).Append(' ');
            AppendList(b, Args).Append(' ').Append(Column);
            return b.ToString();
        }

    }

}
