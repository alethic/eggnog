namespace Eggnog
{

    /// <summary>
    /// How a table is defined, beyond its name and columns.
    /// </summary>
    public sealed class TableOptions
    {

        /// <summary>
        /// Gets the role of a relation or function: <see cref="TableRole.Fact"/> (the default) or
        /// <see cref="TableRole.Analysis"/>; <see cref="TableRole.Term"/> is rejected. Ignored for constructors, which
        /// are terms.
        /// </summary>
        public TableRole Role { get; init; } = TableRole.Fact;

        /// <summary>
        /// Gets whether lifts copy the table into a case's parent. <c>null</c>: facts yes, analyses no, constructors
        /// yes (term lift).
        /// </summary>
        public bool? Lift { get; init; }

        /// <summary>
        /// Gets, for a constructor, whether extraction may use its rows; <c>false</c> makes extraction skip them.
        /// </summary>
        public bool Extractable { get; init; } = true;

        /// <summary>
        /// Gets whether rows of the table may be subsumed (egglog's <c>subsume</c>): by a rule's <see
        /// cref="RuleBuilder.Subsume{TSort}(ConstructorTerm{TSort})"/>, <see cref="RuleBuilder.Subsume(Fact[])"/> or
        /// <see cref="RuleBuilder.Rewrite{TSort}(Term{TSort}, Term{TSort}, bool)"/>, or by a host's <see
        /// cref="Scope.Subsume{TSort}(ConstructorTerm{TSort})"/>, <see cref="Scope.Subsume(Fact[])"/> or the same calls
        /// of a <see cref="ScopeWriter"/>. A subsumed row stays in the table and is still found by lookups, checks and
        /// host reads, but rule bodies, queries and split rules no longer match it, and extraction skips it.
        /// Subsumption is sticky: nothing makes a row unsubsumed. Only relations and constructors can be subsumable, as
        /// in egglog, whose language refuses to subsume a function. The default, <c>null</c>, is egglog's: every relation
        /// and constructor is subsumable, and no function. <c>false</c> opts a relation or constructor out, an addition:
        /// it stores no subsumption cell, so a table that is never subsumed pays nothing for it, and refuses to be
        /// subsumed; <c>true</c> for a function is refused.
        /// </summary>
        public bool? Subsumable { get; init; }

    }

}
