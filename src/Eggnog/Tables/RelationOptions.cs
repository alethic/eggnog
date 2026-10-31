namespace Eggnog
{

    /// <summary>
    /// How a relation is defined, beyond its name and columns.
    /// </summary>
    public sealed class RelationOptions
    {

        /// <summary>
        /// Gets whether the relation is an analysis (<see cref="TableRole.Analysis"/>), derived from terms only, rather
        /// than a fact (<see cref="TableRole.Fact"/>, the default).
        /// </summary>
        public bool Analysis { get; init; }

        /// <summary>
        /// Gets whether lifts copy the relation into a case's parent. <c>null</c>, the default: a fact yes, an analysis
        /// no.
        /// </summary>
        public bool? Lift { get; init; }

        /// <summary>
        /// Gets whether rows of the relation may be subsumed (egglog's <c>subsume</c>): by a rule's <see
        /// cref="RuleBuilder.Subsume(Fact[])"/>, or by a host's <see cref="Scope.Subsume(Fact[])"/> or the same call of a
        /// <see cref="ScopeWriter"/>. A subsumed row stays in the table and is still found by lookups, checks and host
        /// reads, but rule bodies, queries and split rules no longer match it. Subsumption is sticky: nothing makes a row
        /// unsubsumed. The default, <c>true</c>, is egglog's: every relation is subsumable. <c>false</c> opts it out, an
        /// addition: it stores no subsumption cell, so a relation that is never subsumed pays nothing for it, and refuses
        /// to be subsumed.
        /// </summary>
        public bool Subsumable { get; init; } = true;

    }

}
