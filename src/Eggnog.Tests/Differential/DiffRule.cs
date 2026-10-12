using System.Collections.Generic;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A rule or a query of a random program: its declared variables, body and head (a query has no head; it collects
    /// its variables). Immutable once generated.
    /// </summary>
    internal sealed class DiffRule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The rule's or query's name.</param>
        /// <param name="ruleset">The ruleset that schedules a rule; -1 for a query.</param>
        /// <param name="naive">Whether the rule is naive.</param>
        /// <param name="variables">The declared variables, in declaration order.</param>
        /// <param name="body">The body facts.</param>
        /// <param name="head">The head items, in order.</param>
        public DiffRule(string name, int ruleset, bool naive, List<(string Name, DiffType Type)> variables, List<DiffFact> body, List<DiffHead> head)
        {
            Name = name;
            Ruleset = ruleset;
            Naive = naive;
            Variables = variables;
            Body = body;
            Head = head;
        }

        /// <summary>Gets the rule's or query's name.</summary>
        public string Name { get; }

        /// <summary>Gets the ruleset that schedules a rule; -1 for a query.</summary>
        public int Ruleset { get; }

        /// <summary>Gets whether the rule is naive.</summary>
        public bool Naive { get; }

        /// <summary>Gets the declared variables, in declaration order: for a query, the columns of a match.</summary>
        public List<(string Name, DiffType Type)> Variables { get; }

        /// <summary>Gets the body facts.</summary>
        public List<DiffFact> Body { get; }

        /// <summary>Gets the head items, in order.</summary>
        public List<DiffHead> Head { get; }

        /// <summary>Gets whether this is a query.</summary>
        public bool IsQuery => Ruleset < 0;

        /// <inheritdoc />
        public override string ToString() => Name;

    }

}
