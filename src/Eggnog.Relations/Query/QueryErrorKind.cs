namespace Eggnog.Relations
{

    /// <summary>
    /// What a <see cref="QueryException"/> reports: the variants of egglog's <c>QueryError</c> that its builders produce
    /// (core-relations/src/query.rs:466-512).
    /// </summary>
    /// <remarks>
    /// egglog also declares <c>CounterUsedInBaseColumn</c> and <c>InvalidSchema</c>, which nothing in core-relations
    /// returns; they have no counterpart here.
    /// </remarks>
    internal enum QueryErrorKind : byte
    {

        /// <summary>
        /// A key passed to a lookup or a removal does not have one entry per key column of the table. egglog's
        /// <c>KeyArityMismatch</c>.
        /// </summary>
        KeyArityMismatch,

        /// <summary>
        /// A row passed to an insertion does not have one entry per column, or the defaults of a lookup-or-insert do
        /// not fill the value columns exactly. egglog's <c>TableArityMismatch</c>.
        /// </summary>
        TableArityMismatch,

        /// <summary>
        /// The two operand lists of <see cref="RuleBuilder.AssertAnyNe"/> have different lengths. egglog's
        /// <c>MultiComparisonMismatch</c>.
        /// </summary>
        MultiComparisonMismatch,

        /// <summary>
        /// An atom does not have one entry per column of its table. egglog's <c>BadArity</c>.
        /// </summary>
        BadArity,

        /// <summary>
        /// A constraint on an atom names a column the table does not have. egglog's <c>InvalidConstraint</c>.
        /// </summary>
        InvalidConstraint,

    }

}
