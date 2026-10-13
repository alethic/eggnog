using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks <see cref="Key"/> up in <see cref="Table"/> and binds <see cref="Column"/> of the row to
    /// <see cref="Destination"/>; where the key is missing, calls <see cref="Function"/> on <see cref="FunctionArgs"/>
    /// and binds its result instead, and a lane where both fail halts. egglog's <c>Instr::LookupWithFallback</c>
    /// (core-relations/src/action/mod.rs:916-927, 737-773); the bridge uses it for functions without a default, with a
    /// panic function as the fallback.
    /// </summary>
    internal sealed class LookupWithFallbackInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="key">The key; one operand per key column.</param>
        /// <param name="function">The fallback.</param>
        /// <param name="functionArgs">The fallback's arguments.</param>
        /// <param name="column">The column of the row to bind.</param>
        /// <param name="destination">The variable defined.</param>
        public LookupWithFallbackInstr(TableId table, ReadOnlySpan<QueryEntry> key, ExternalFunctionId function, ReadOnlySpan<QueryEntry> functionArgs, ColumnId column, Variable destination)
            : base(InstrKind.LookupWithFallback)
        {
            Table = table;
            Key = CopyOperands(key, nameof(key));
            Function = function;
            FunctionArgs = CopyOperands(functionArgs, nameof(functionArgs));
            Column = CheckColumn(column, nameof(column));
            Destination = CheckDestination(destination, nameof(destination));
            CheckNotReading(FunctionArgs, destination, nameof(functionArgs));
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public TableId Table { get; }

        /// <summary>
        /// Gets the key.
        /// </summary>
        public QueryEntry[] Key { get; }

        /// <summary>
        /// Gets the fallback.
        /// </summary>
        public ExternalFunctionId Function { get; }

        /// <summary>
        /// Gets the fallback's arguments.
        /// </summary>
        public QueryEntry[] FunctionArgs { get; }

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
            b.Append(Destination).Append(" <- LookupWithFallback ").Append(Table).Append(' ');
            AppendList(b, Key).Append(' ').Append(Column).Append(" ?? ").Append(Function).Append(' ');
            AppendList(b, FunctionArgs);
            return b.ToString();
        }

    }

}
