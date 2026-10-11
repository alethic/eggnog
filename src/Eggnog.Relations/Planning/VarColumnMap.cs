using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// The variables of one atom and the columns that hold them: egglog's <c>VarColumnMap</c>
    /// (core-relations/src/query.rs:902-950).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two maps: column to variable (every column that holds a variable; constants and wildcards hold none) and
    /// variable to column. For a repeated variable the variable-to-column map keeps the <em>last</em> column inserted,
    /// as egglog's does, while enumeration (<see cref="GetEnumerator"/>) yields the variable once per column, in column
    /// order.
    /// </para>
    /// <para>
    /// Written only while its atom is built, then read-only: plan templates share it.
    /// </para>
    /// </remarks>
    internal sealed class VarColumnMap
    {

        readonly DenseIdMap<Variable, ColumnId> varToColumn = new();
        readonly DenseIdMap<ColumnId, Variable> columnToVar = new();

        /// <summary>
        /// Gets the number of columns that hold a variable.
        /// </summary>
        public int ColumnCount => columnToVar.Count;

        /// <summary>
        /// Gets whether no column holds a variable.
        /// </summary>
        public bool IsEmpty => varToColumn.IsEmpty;

        /// <summary>
        /// Records that <paramref name="column"/> holds <paramref name="variable"/>. egglog's <c>insert</c>.
        /// </summary>
        /// <param name="variable">The variable; not the wildcard.</param>
        /// <param name="column">The column.</param>
        /// <param name="previous">The column previously recorded for the variable, when the result is <c>true</c>.</param>
        /// <returns><c>true</c> if the variable already had a column (it is repeated in the atom).</returns>
        public bool Insert(Variable variable, ColumnId column, out ColumnId previous)
        {
            if (variable.IsPlaceholder)
                throw new ArgumentException("A wildcard does not occupy a column.", nameof(variable));

            var had = varToColumn.TryGetValue(variable, out previous);
            varToColumn.Insert(variable, column);
            columnToVar.Insert(column, variable);
            return had;
        }

        /// <summary>
        /// Gets the column of <paramref name="variable"/>: the last one inserted for a repeated variable. egglog's
        /// <c>get_col</c>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <param name="column">The column.</param>
        /// <returns><c>true</c> if the atom holds the variable.</returns>
        public bool TryGetColumn(Variable variable, out ColumnId column) => varToColumn.TryGetValue(variable, out column);

        /// <summary>
        /// Gets the variable held by <paramref name="column"/>. egglog's <c>get_var</c>.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="variable">The variable.</param>
        /// <returns><c>true</c> if the column holds a variable.</returns>
        public bool TryGetVariable(ColumnId column, out Variable variable) => columnToVar.TryGetValue(column, out variable);

        /// <summary>
        /// Gets whether the atom holds <paramref name="variable"/>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns><c>true</c> if some column holds it.</returns>
        public bool ContainsVariable(Variable variable) => varToColumn.ContainsKey(variable);

        /// <summary>
        /// Enumerates the (column, variable) pairs in column order; a repeated variable appears once per column.
        /// egglog's <c>iter</c> / <c>vars</c>.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public DenseIdMapEnumerator<ColumnId, Variable> GetEnumerator() => columnToVar.GetEnumerator();

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append('(');
            var first = true;
            foreach (var (column, variable) in columnToVar)
            {
                if (first == false)
                    b.Append(", ");
                b.Append(column).Append("->").Append(variable);
                first = false;
            }

            return b.Append(')').ToString();
        }

    }

}
