using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A rule passed to <see cref="RuleQueryBuilder"/> or <see cref="RuleBuilder"/> does not fit the schema of the tables
    /// it names: egglog's <c>QueryError</c> (core-relations/src/query.rs:466-512), raised where egglog returns
    /// <c>Err</c>.
    /// </summary>
    /// <remarks>
    /// An <see cref="ArgumentException"/>, as bad input at an API edge is everywhere in the engine. The builder that
    /// throws it is left exactly as it was before the call, as egglog validates before it changes anything.
    /// <see cref="Kind"/> says which check failed; <see cref="Expected"/> and <see cref="Actual"/> carry egglog's
    /// <c>expected</c>/<c>got</c> (or <c>arity</c>/<c>column</c>, or <c>l</c>/<c>r</c>) fields.
    /// </remarks>
    internal sealed class QueryException : ArgumentException
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kind">The check that failed.</param>
        /// <param name="message">The message, worded as egglog's.</param>
        /// <param name="table">The table involved, if any.</param>
        /// <param name="expected">What the schema requires.</param>
        /// <param name="actual">What the rule supplied.</param>
        /// <param name="constraint">The offending constraint of an <see cref="QueryErrorKind.InvalidConstraint"/>.</param>
        public QueryException(QueryErrorKind kind, string message, TableId? table, int expected, int actual, Constraint? constraint = null)
            : base(message)
        {
            Kind = kind;
            Table = table;
            Expected = expected;
            Actual = actual;
            Constraint = constraint;
        }

        /// <summary>
        /// Gets the check that failed.
        /// </summary>
        public QueryErrorKind Kind { get; }

        /// <summary>
        /// Gets the table involved, or <c>null</c> for <see cref="QueryErrorKind.MultiComparisonMismatch"/>.
        /// </summary>
        public TableId? Table { get; }

        /// <summary>
        /// Gets what the schema requires: the key count, the arity, or the length of the left operand list.
        /// </summary>
        public int Expected { get; }

        /// <summary>
        /// Gets what the rule supplied: the entry count, the first column out of range, or the length of the right
        /// operand list.
        /// </summary>
        public int Actual { get; }

        /// <summary>
        /// Gets the offending constraint of an <see cref="QueryErrorKind.InvalidConstraint"/>, otherwise <c>null</c>.
        /// </summary>
        public Constraint? Constraint { get; }

        /// <summary>
        /// Creates egglog's <c>KeyArityMismatch</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="expected">The table's key count.</param>
        /// <param name="got">The number of key entries supplied.</param>
        /// <returns>The exception.</returns>
        public static QueryException KeyArityMismatch(TableId table, int expected, int got) =>
            new(QueryErrorKind.KeyArityMismatch, $"table {table} has {expected} keys but got {got}", table, expected, got);

        /// <summary>
        /// Creates egglog's <c>TableArityMismatch</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="expected">The table's arity.</param>
        /// <param name="got">The number of columns supplied, or the first column out of range.</param>
        /// <returns>The exception.</returns>
        public static QueryException TableArityMismatch(TableId table, int expected, int got) =>
            new(QueryErrorKind.TableArityMismatch, $"table {table} has {expected} columns but got {got}", table, expected, got);

        /// <summary>
        /// Creates egglog's <c>MultiComparisonMismatch</c>.
        /// </summary>
        /// <param name="left">The length of the left operand list.</param>
        /// <param name="right">The length of the right operand list.</param>
        /// <returns>The exception.</returns>
        public static QueryException MultiComparisonMismatch(int left, int right) =>
            new(QueryErrorKind.MultiComparisonMismatch, $"attempt to compare two groups of values, one of length {left}, another of length {right}", null, left, right);

        /// <summary>
        /// Creates egglog's <c>BadArity</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="expected">The table's arity.</param>
        /// <param name="got">The number of entries of the atom.</param>
        /// <returns>The exception.</returns>
        public static QueryException BadArity(TableId table, int expected, int got) =>
            new(QueryErrorKind.BadArity, $"table {table} expected {expected} columns but got {got}", table, expected, got);

        /// <summary>
        /// Creates egglog's <c>InvalidConstraint</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraint">The constraint.</param>
        /// <param name="column">The column it names that the table lacks.</param>
        /// <param name="arity">The table's arity.</param>
        /// <returns>The exception.</returns>
        public static QueryException InvalidConstraint(TableId table, Constraint constraint, int column, int arity) =>
            new(QueryErrorKind.InvalidConstraint, $"constraint {constraint} on table {table} references column {column}, but the table has arity {arity}", table, arity, column, constraint);

    }

}
