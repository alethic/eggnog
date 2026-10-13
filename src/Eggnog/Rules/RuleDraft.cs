using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// What a <see cref="RuleBuilder"/> collected, before any check: the compiler reads it once, and the rule keeps it for
    /// rendering the rule as written.
    /// </summary>
    internal sealed class RuleDraft
    {

        /// <summary>
        /// Initializes an empty draft.
        /// </summary>
        /// <param name="name">The rule's name.</param>
        /// <param name="owner">The builder; its variables carry it.</param>
        public RuleDraft(string name, object owner)
        {
            Name = name;
            Owner = owner;
        }

        /// <summary>
        /// Gets the rule's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the builder whose variables the draft may use.
        /// </summary>
        public object Owner { get; }

        /// <summary>
        /// Gets the declared body variables in declaration order: name, column type (<c>null</c> when the type was unknown
        /// at declaration; the compiler reports it), and the variable.
        /// </summary>
        public List<(string Name, ColumnType? Type, Term Variable)> Variables { get; } = new();

        /// <summary>
        /// Gets the body facts (When facts and Where filters), in call order.
        /// </summary>
        public List<Fact> Body { get; } = new();

        /// <summary>
        /// Gets the head items, in call order.
        /// </summary>
        public List<HeadItem> Head { get; } = new();

        /// <summary>
        /// Gets or sets whether every run matches in full (a naive rule).
        /// </summary>
        public bool Naive { get; set; }

        /// <summary>
        /// Gets or sets the licence the rule's own <see cref="RuleBuilder.Erasing"/> call gave it; <c>null</c> when it takes
        /// its ruleset's.
        /// </summary>
        public ScopeTag? Erasing { get; set; }

        /// <summary>
        /// Gets or sets the reason given to <see cref="RuleBuilder.ReadsOwnFacts"/>, which exempts the rule from its
        /// licence; <c>null</c> when the rule did not call it.
        /// </summary>
        public string? OwnFactsReason { get; set; }

    }

}
