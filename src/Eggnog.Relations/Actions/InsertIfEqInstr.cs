using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Stages the row <see cref="Values"/> into <see cref="Table"/> in every live lane where <see cref="Left"/> equals
    /// <see cref="Right"/>. egglog's <c>Instr::InsertIfEq</c> (core-relations/src/action/mod.rs:936-942, 781-811); the
    /// bridge subsumes rows with it. Never halts a lane.
    /// </summary>
    internal sealed class InsertIfEqInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="left">The first operand compared.</param>
        /// <param name="right">The second operand compared.</param>
        /// <param name="values">The row; one operand per column.</param>
        public InsertIfEqInstr(TableId table, QueryEntry left, QueryEntry right, ReadOnlySpan<QueryEntry> values)
            : base(InstrKind.InsertIfEq)
        {
            Table = table;
            Left = CheckOperand(left, nameof(left));
            Right = CheckOperand(right, nameof(right));
            Values = CopyOperands(values, nameof(values));
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public TableId Table { get; }

        /// <summary>
        /// Gets the first operand compared.
        /// </summary>
        public QueryEntry Left { get; }

        /// <summary>
        /// Gets the second operand compared.
        /// </summary>
        public QueryEntry Right { get; }

        /// <summary>
        /// Gets the row.
        /// </summary>
        public QueryEntry[] Values { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder("InsertIfEq ").Append(Table).Append(' ').Append(Left).Append(" == ").Append(Right).Append(' ');
            return AppendList(b, Values).ToString();
        }

    }

}
