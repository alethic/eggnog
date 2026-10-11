using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks <see cref="Args"/> up in <see cref="Table"/> and binds <see cref="Column"/> of the row to
    /// <see cref="Destination"/>, or <see cref="Default"/> when the key is missing. egglog's
    /// <c>Instr::LookupWithDefault</c> (core-relations/src/action/mod.rs:897-905, 715-726); the bridge reads the
    /// union-find with it. Never halts a lane.
    /// </summary>
    internal sealed class LookupWithDefaultInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="args">The key; one operand per key column.</param>
        /// <param name="column">The column of the row to bind.</param>
        /// <param name="destination">The variable defined.</param>
        /// <param name="default">The value bound when the key is missing.</param>
        public LookupWithDefaultInstr(TableId table, ReadOnlySpan<QueryEntry> args, ColumnId column, Variable destination, QueryEntry @default)
            : base(InstrKind.LookupWithDefault)
        {
            Table = table;
            Args = CopyOperands(args, nameof(args));
            Column = CheckColumn(column, nameof(column));
            Destination = CheckDestination(destination, nameof(destination));
            Default = CheckOperand(@default, nameof(@default));
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

        /// <summary>
        /// Gets the value bound when the key is missing.
        /// </summary>
        public QueryEntry Default { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(Destination).Append(" <- LookupWithDefault ").Append(Table).Append(' ');
            AppendList(b, Args).Append(' ').Append(Column).Append(" ?? ").Append(Default);
            return b.ToString();
        }

    }

}
