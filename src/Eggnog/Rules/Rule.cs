using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A compiled rule. Made by <see cref="Ruleset.DefineRule(string, System.Action{RuleBuilder})"/>.
    /// </summary>
    public sealed class Rule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The rule's name.</param>
        /// <param name="ruleset">The ruleset that schedules it.</param>
        /// <param name="owner">The scope it is local to; <c>null</c> for a global rule.</param>
        /// <param name="isNaive">Whether every run matches in full.</param>
        /// <param name="draft">What its builder collected, for rendering.</param>
        /// <param name="licence">The effective licence: its own, else its ruleset's; <c>null</c> when exempt.</param>
        /// <param name="readScopes">The distinct tags its scoped atoms read, in first-use order.</param>
        internal Rule(string name, Ruleset ruleset, Scope? owner, bool isNaive, RuleDraft draft, ScopeTag? licence, IReadOnlyList<ScopeTag> readScopes)
        {
            Name = name;
            Ruleset = ruleset;
            Owner = owner;
            IsNaive = isNaive;
            Draft = draft;
            Licence = licence;
            ReadScopes = readScopes;
        }

        /// <summary>
        /// Gets the rule's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the ruleset that schedules the rule.
        /// </summary>
        public Ruleset Ruleset { get; }

        /// <summary>
        /// Gets the scope the rule is local to (§6.7): it is active in that scope and its descendants. <c>null</c> for a
        /// global rule.
        /// </summary>
        public Scope? Owner { get; }

        /// <summary>
        /// Gets whether every run matches in full (a naive engine rule, which never inherits watermarks).
        /// </summary>
        public bool IsNaive { get; }

        /// <summary>
        /// Gets the rule's effective licence (D12): its own <see cref="RuleBuilder.Erasing"/> tag, else its ruleset's;
        /// <c>null</c> when it has none, or when <see cref="RuleBuilder.ReadsOwnFacts"/> exempts it.
        /// </summary>
        public ScopeTag? Licence { get; }

        /// <summary>
        /// Gets the reason given to <see cref="RuleBuilder.ReadsOwnFacts"/>; <c>null</c> when the rule does not use it.
        /// </summary>
        public string? OwnFactsReason => Draft.OwnFactsReason;

        /// <summary>
        /// Gets the distinct tags the rule's scoped atoms read, in first-use order.
        /// </summary>
        public IReadOnlyList<ScopeTag> ReadScopes { get; }

        /// <summary>
        /// Renders the rule as written, as an s-expression:
        /// <c>(rule ((= e (UuidEq p v)) (form (Query) p "uuid-lower")) ((union e (StrEq p v))) :ruleset rewrite)</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => SExpression.Render(this);

        /// <summary>
        /// Gets the compiled rule; set once by the compiler.
        /// </summary>
        internal CompiledRule Compiled { get; set; } = null!;

        /// <summary>
        /// Gets what the rule's builder collected.
        /// </summary>
        internal RuleDraft Draft { get; }

    }

}
