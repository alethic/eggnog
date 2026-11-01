namespace Eggnog
{

    /// <summary>
    /// How a constructor is defined, beyond its name, columns and cost. A constructor's role is always
    /// <see cref="TableRole.Term"/>.
    /// </summary>
    public sealed class ConstructorOptions
    {

        /// <summary>
        /// Gets whether extraction skips the constructor's rows (egglog's <c>:unextractable</c>); the default is
        /// <c>false</c>. <see cref="CostModel.Unextractable"/> skips a constructor for one extraction instead.
        /// </summary>
        public bool Unextractable { get; init; }

        /// <summary>
        /// Gets whether the constructor's terms may be subsumed (egglog's <c>subsume</c>): by a rule's <see
        /// cref="RuleBuilder.Subsume{TSort}(ConstructorTerm{TSort})"/> or <see
        /// cref="RuleBuilder.Rewrite{TSort}(Term{TSort}, Term{TSort}, bool)"/>, or by a host's <see
        /// cref="Scope.Subsume{TSort}(ConstructorTerm{TSort})"/> or the same call of a <see cref="ScopeWriter"/>. A
        /// subsumed row stays in the table and is still found by lookups, checks and host reads, but rule bodies,
        /// queries and split rules no longer match it, and extraction skips it. Subsumption is sticky: nothing makes a
        /// row unsubsumed. The default, <c>true</c>, is egglog's: every constructor is subsumable. <c>false</c> opts it
        /// out, an addition: it stores no subsumption cell, so a constructor that is never subsumed pays nothing for it,
        /// and refuses to be subsumed.
        /// </summary>
        public bool Subsumable { get; init; } = true;

        /// <summary>
        /// Gets whether lifts copy the constructor's terms into a case's parent (term lift); the default is <c>true</c>.
        /// </summary>
        public bool Lift { get; init; } = true;

    }

}
