using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Looks <see cref="Args"/> up in <see cref="Table"/> and binds <see cref="Column"/> of the row to
    /// <see cref="Destination"/>; a key the table lacks gets the row <c>Args ++ Defaults</c>, staged once per execution
    /// state and remembered in its predicted-value cache. egglog's <c>Instr::LookupOrInsertDefault</c>
    /// (core-relations/src/action/mod.rs:887-895, 643-714): the instruction behind constructors, which mint fresh ids.
    /// Never halts a lane.
    /// </summary>
    /// <remarks>
    /// Running it sets <see cref="ExecutionState.Changed"/> when it stages a row; egglog's does not
    /// (core-relations/src/action/mod.rs:707), so there a merge function that inserts through it under-reports its
    /// change.
    /// </remarks>
    internal sealed class LookupOrInsertDefaultInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="args">The key; one operand per key column.</param>
        /// <param name="defaults">The value columns of a new row, in column order.</param>
        /// <param name="column">The column of the row to bind; below the key and default count.</param>
        /// <param name="destination">The variable defined.</param>
        public LookupOrInsertDefaultInstr(TableId table, ReadOnlySpan<QueryEntry> args, ReadOnlySpan<WriteVal> defaults, ColumnId column, Variable destination)
            : base(InstrKind.LookupOrInsertDefault)
        {
            Table = table;
            Args = CopyOperands(args, nameof(args));
            Defaults = defaults.ToArray();
            for (var i = 0; i < Defaults.Length; i++)
            {
                var d = Defaults[i];
                switch (d.Kind)
                {
                    case WriteValKind.Entry:
                        CheckOperand(d.Entry, nameof(defaults));
                        break;
                    case WriteValKind.CurrentValue:
                        // egglog reads row[ix] of the row built so far, which panics unless ix comes before this cell.
                        if ((uint)d.Column >= (uint)(Args.Length + i))
                            throw new ArgumentOutOfRangeException(nameof(defaults), d.Column, "A current-value default must copy an earlier cell of the row.");
                        break;
                }
            }

            Column = CheckColumn(column, nameof(column));
            if (column.Rep >= Args.Length + Defaults.Length)
                throw new ArgumentOutOfRangeException(nameof(column), column, "The column lies outside the row the instruction builds.");

            Destination = CheckDestination(destination, nameof(destination));
            CheckNotReading(Args, destination, nameof(args));
            foreach (var d in Defaults)
                if (d.Kind == WriteValKind.Entry && d.Entry.IsVariable && d.Entry.Variable == destination)
                    throw new ArgumentException($"The defaults cannot read the destination {destination}.", nameof(defaults));
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
        /// Gets the value columns of a new row.
        /// </summary>
        public WriteVal[] Defaults { get; }

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
            b.Append(Destination).Append(" <- LookupOrInsertDefault ").Append(Table).Append(' ');
            AppendList(b, Args).Append(' ');
            AppendList(b, Defaults).Append(' ').Append(Column);
            return b.ToString();
        }

    }

}
