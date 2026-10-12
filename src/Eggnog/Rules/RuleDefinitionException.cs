using System;

namespace Eggnog
{

    /// <summary>
    /// A rule that cannot be compiled: the message says what is wrong (the messages of the rule-layer spec §6.3, exact),
    /// and <see cref="RuleName"/> which rule. Nothing is registered when a definition throws it.
    /// </summary>
    public sealed class RuleDefinitionException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="ruleName">The rule's name.</param>
        /// <param name="message">What is wrong.</param>
        /// <param name="inner">The exception that caused it, if any.</param>
        public RuleDefinitionException(string ruleName, string message, Exception? inner = null)
            : base(message, inner)
        {
            RuleName = ruleName;
        }

        /// <summary>
        /// Gets the rule's name.
        /// </summary>
        public string RuleName { get; }

    }

}
