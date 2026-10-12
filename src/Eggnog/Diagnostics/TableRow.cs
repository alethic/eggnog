using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// One decoded row of a table, from <see cref="Scope.Rows(TableHandle)"/> (§6.11): for debugging and tests. Immutable.
    /// </summary>
    public sealed class TableRow
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="keys">The decoded keys; owned by the new instance.</param>
        /// <param name="output">The decoded value or class; <c>null</c> for a relation.</param>
        internal TableRow(TableHandle table, object[] keys, object? output)
        {
            Table = table;
            Keys = Array.AsReadOnly(keys);
            Output = output;
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public TableHandle Table { get; }

        /// <summary>
        /// Gets the decoded keys, one per key column; a sort cell is its <see cref="ClassId"/>, canonical in the scope read
        /// and carrying it as its origin (D39).
        /// </summary>
        public IReadOnlyList<object> Keys { get; }

        /// <summary>
        /// Gets the function's value or the constructor's class (a <see cref="ClassId"/>); <c>null</c> for a relation.
        /// </summary>
        public object? Output { get; }

        /// <summary>
        /// Renders the row as an s-expression: <c>(present "kind")</c>, <c>(dom "kind") = (set-of "a" "b")</c>,
        /// <c>(StrEq "kind" "a") = #17</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => SExpression.Render(this);

    }

}
