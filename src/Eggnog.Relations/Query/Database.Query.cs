using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// The database's side of rule building: the planner's schema (<see cref="IQuerySchema"/>), statistics
    /// (<see cref="IPlanStatistics"/>) and header evaluation (<see cref="IHeaderEvaluator"/>), all answered from the
    /// tables and their cached indexes, plus egglog's <c>new_rule_set</c> and <c>plan_query</c>
    /// (core-relations/src/free_join/mod.rs:324-326, 818-820).
    /// </summary>
    /// <remarks>
    /// The planner is a pure function with no notion of a database; these explicit interface implementations are how it
    /// reads one. Each is egglog's <c>process_constraints</c> (core-relations/src/free_join/mod.rs:718-761) or
    /// <c>ColumnCardEst</c> seen through a narrower contract. All run on cold paths, and like
    /// <see cref="ProcessConstraints(TableId, ReadOnlySpan{Constraint}, List{Constraint}, List{Constraint})"/> they may
    /// build a cached column index on first use, a planning side effect egglog has too. They need the database not to
    /// merge or rebuild meanwhile.
    /// </remarks>
    internal sealed partial class Database : IQuerySchema, IPlanStatistics, IHeaderEvaluator
    {

        /// <summary>
        /// Starts building a rule set over this database. egglog's <c>new_rule_set</c>.
        /// </summary>
        /// <returns>The builder.</returns>
        public RuleSetBuilder NewRuleSet() => new(this);

        /// <summary>
        /// Plans <paramref name="query"/> with the statistics of the current tables. egglog's <c>plan_query</c>.
        /// </summary>
        /// <param name="query">The query; not modified.</param>
        /// <returns>The plan template.</returns>
        internal PlanTemplate PlanQuery(Query query) => QueryPlanner.PlanQuery(query, this);

        /// <summary>
        /// Gets the columns of <paramref name="table"/> that hold e-class ids: the columns its rebuild canonicalizes
        /// (<see cref="SortedWritesTable.RebuildColumns"/>). The union-find table has none: its rows record displaced
        /// ids. Used to recognize constant ids in rules (<see cref="IdConstant"/>).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The columns; empty when the table rebuilds nothing.</returns>
        /// <exception cref="KeyNotFoundException">No such table.</exception>
        internal ReadOnlySpan<ColumnId> GetIdColumns(TableId table)
        {
            return GetTable(table) is SortedWritesTable sorted ? sorted.RebuildColumns : ReadOnlySpan<ColumnId>.Empty;
        }

        /// <inheritdoc />
        int IQuerySchema.GetArity(TableId table) => GetTableInfo(table).Spec.Arity;

        /// <inheritdoc />
        int IQuerySchema.GetKeyCount(TableId table) => GetTableInfo(table).Spec.KeyCount;

        /// <summary>
        /// Splits an atom's constraints exactly as
        /// <see cref="ProcessConstraints(TableId, ReadOnlySpan{Constraint}, List{Constraint}, List{Constraint})"/> does
        /// (the table's own fast constraints, then <see cref="ConstraintKind.EqConst"/>s on cacheable columns promoted
        /// through a column index; everything else slow, in order) and drops the subset: egglog's <c>add_atom</c>
        /// calls <c>process_constraints</c> and keeps its split (core-relations/src/query.rs:407).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the fast constraints.</param>
        /// <param name="slow">Receives the slow constraints.</param>
        void IQuerySchema.SplitConstraints(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow)
        {
            ProcessConstraints(table, constraints, fast, slow).Dispose();
        }

        /// <summary>
        /// Gets the number of rows of the atom's table that match its fast constraints, tombstones included: egglog's
        /// <c>approx_size</c>, the size of the subset <c>process_constraints</c> computed for the atom
        /// (core-relations/src/free_join/mod.rs:91-94).
        /// </summary>
        /// <param name="atom">The atom's id.</param>
        /// <param name="info">The atom.</param>
        /// <returns>The estimate.</returns>
        long IPlanStatistics.GetApproximateSize(AtomId atom, QueryAtom info)
        {
            ArgumentNullException.ThrowIfNull(info);
            var fast = new List<Constraint>(info.FastConstraints.Length);
            var slow = new List<Constraint>();
            var subset = ProcessConstraints(info.Table, info.FastConstraints, fast, slow);
            var size = subset.Size;
            subset.Dispose();
            return size;
        }

        /// <summary>
        /// Gets the table's live row count and the column's distinct-value count from its cached column index. egglog's
        /// <c>col_uniqueness</c> (core-relations/src/free_join/mod.rs:888-895), through
        /// <see cref="ColumnCardEstimator"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="column">The column.</param>
        /// <returns>The estimate.</returns>
        ColumnUniqueness IPlanStatistics.GetColumnUniqueness(TableId table, ColumnId column) => new ColumnCardEstimator(this).GetColumnUniqueness(table, column);

        /// <summary>
        /// Evaluates a header's constraints against the current table:
        /// <see cref="ProcessConstraints(TableId, ReadOnlySpan{Constraint}, List{Constraint}, List{Constraint})"/>,
        /// egglog's <c>reprocess_constraints</c> core (core-relations/src/query.rs:133-154).
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraints">The constraints.</param>
        /// <param name="fast">Receives the fast constraints.</param>
        /// <param name="slow">Receives the slow constraints.</param>
        /// <returns>The rows matching every fast constraint, which the caller owns.</returns>
        Subset IHeaderEvaluator.Evaluate(TableId table, ReadOnlySpan<Constraint> constraints, List<Constraint> fast, List<Constraint> slow)
        {
            return ProcessConstraints(table, constraints, fast, slow);
        }

    }

}
