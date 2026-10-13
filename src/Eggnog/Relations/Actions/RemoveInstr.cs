using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Stages the removal of the row with key <see cref="Args"/> from <see cref="Table"/> in every live lane. egglog's
    /// <c>Instr::Remove</c> (core-relations/src/action/mod.rs:944-948, 812-818): <c>delete</c> and the first half of a
    /// rebuild's row rewrite in the bridge.
    /// </summary>
    internal sealed class RemoveInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="args">The key; one operand per key column.</param>
        public RemoveInstr(TableId table, ReadOnlySpan<QueryEntry> args)
            : base(InstrKind.Remove)
        {
            Table = table;
            Args = CopyOperands(args, nameof(args));
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public TableId Table { get; }

        /// <summary>
        /// Gets the key.
        /// </summary>
        public QueryEntry[] Args { get; }

        /// <inheritdoc />
        public override string ToString() => AppendList(new StringBuilder("Remove ").Append(Table).Append(' '), Args).ToString();

    }

}
