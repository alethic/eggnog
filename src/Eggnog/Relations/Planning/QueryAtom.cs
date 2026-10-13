using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// One atom of a conjunctive query: a table, the variables in its columns, and its constraints split into fast and
    /// slow ones. egglog's <c>Atom</c> with its <c>ProcessedConstraints</c> (core-relations/src/query.rs:876-900,
    /// core-relations/src/free_join/mod.rs:70-104), minus the row subset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// egglog keeps the subset of rows matching the fast constraints inside the atom, and so inside every plan. That
    /// subset is only meaningful for one physical table state, so here the atom holds only the constraints: the subset
    /// is computed per run when a plan is instantiated (<see cref="PlanTemplate.Instantiate{TEvaluator}"/>), and the
    /// planner reads its size estimate from <see cref="IPlanStatistics"/>. That keeps atoms, and the plan templates
    /// holding them, independent of data and shareable across runs and contexts.
    /// </para>
    /// <para>
    /// Fast constraints (a range on a sorted column, an equality on a cacheable column) become
    /// <see cref="JoinHeaderSpec"/>s; slow ones are evaluated on the atom's first scan in plan order. Repeated variables
    /// add slow <see cref="ConstraintKind.Eq"/> constraints, constants add <see cref="ConstraintKind.EqConst"/> ones.
    /// </para>
    /// <para>
    /// Immutable once built. The planner's synthetic covering atoms use <see cref="TableId.Dummy"/> and never reach a
    /// plan.
    /// </para>
    /// </remarks>
    internal sealed class QueryAtom
    {

        /// <summary>
        /// Initializes a new instance. The arrays are owned by the new instance.
        /// </summary>
        /// <param name="table">The table, or <see cref="TableId.Dummy"/> for a synthetic covering atom.</param>
        /// <param name="varColumns">The variables and the columns holding them.</param>
        /// <param name="fastConstraints">The constraints answered by a subset computed up front.</param>
        /// <param name="slowConstraints">The constraints evaluated per row.</param>
        public QueryAtom(TableId table, VarColumnMap varColumns, Constraint[] fastConstraints, Constraint[] slowConstraints)
        {
            ArgumentNullException.ThrowIfNull(varColumns);
            ArgumentNullException.ThrowIfNull(fastConstraints);
            ArgumentNullException.ThrowIfNull(slowConstraints);
            Table = table;
            VarColumns = varColumns;
            FastConstraints = fastConstraints;
            SlowConstraints = slowConstraints;
        }

        /// <summary>
        /// Gets the table.
        /// </summary>
        public TableId Table { get; }

        /// <summary>
        /// Gets whether this is a synthetic covering atom created during tree decomposition.
        /// </summary>
        public bool IsDummy => Table == TableId.Dummy;

        /// <summary>
        /// Gets the variables and the columns that hold them.
        /// </summary>
        public VarColumnMap VarColumns { get; }

        /// <summary>
        /// Gets the fast constraints. Callers must not modify the array.
        /// </summary>
        public Constraint[] FastConstraints { get; }

        /// <summary>
        /// Gets the slow constraints. Callers must not modify the array.
        /// </summary>
        public Constraint[] SlowConstraints { get; }

        /// <summary>
        /// Gets whether no column holds a variable (every entry is a constant or a wildcard).
        /// </summary>
        public bool HasNoVariables => VarColumns.IsEmpty;

        /// <summary>
        /// Gets whether some column holds <paramref name="variable"/>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns><c>true</c> if the atom mentions it.</returns>
        public bool ContainsVariable(Variable variable) => VarColumns.ContainsVariable(variable);

        /// <summary>
        /// Gets the column of <paramref name="variable"/>, the last one for a repeated variable; throws if absent.
        /// egglog's <c>get_col(..).unwrap()</c>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns>The column.</returns>
        public ColumnId GetColumn(Variable variable)
        {
            if (VarColumns.TryGetColumn(variable, out var column) == false)
                throw new InvalidOperationException($"Atom does not hold {variable}.");

            return column;
        }

        /// <summary>
        /// Gets the variable held by <paramref name="column"/>; throws if it holds none. egglog's
        /// <c>get_var(..).unwrap()</c>.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <returns>The variable.</returns>
        public Variable GetVariable(ColumnId column)
        {
            if (VarColumns.TryGetVariable(column, out var variable) == false)
                throw new InvalidOperationException($"Column {column} holds no variable.");

            return variable;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append(Table).Append(VarColumns);
            if (FastConstraints.Length > 0)
                b.Append(" fast[").Append(string.Join(", ", FastConstraints)).Append(']');
            if (SlowConstraints.Length > 0)
                b.Append(" slow[").Append(string.Join(", ", SlowConstraints)).Append(']');

            return b.ToString();
        }

    }

}
