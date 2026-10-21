using System;

namespace Eggnog
{

    /// <summary>
    /// A rule callback as a head item holds it: the delegate of <see cref="RuleBuilder.ThenCall"/> or
    /// <see cref="RuleBuilder.ThenCallFull"/>.
    /// </summary>
    internal sealed class RuleCallbackDefinition
    {

        readonly Action<RuleCallbackContext>? write;
        readonly Action<FullRuleCallbackContext>? full;

        /// <summary>
        /// Initializes a callback that writes.
        /// </summary>
        /// <param name="write">The callback.</param>
        public RuleCallbackDefinition(Action<RuleCallbackContext> write)
        {
            this.write = write;
        }

        /// <summary>
        /// Initializes a callback that reads and writes.
        /// </summary>
        /// <param name="full">The callback.</param>
        public RuleCallbackDefinition(Action<FullRuleCallbackContext> full)
        {
            this.full = full;
        }

        /// <summary>
        /// Gets whether the callback reads tables, as egglog's <c>rust_rule_full</c>'s does.
        /// </summary>
        public bool Full => full is not null;

        /// <summary>
        /// Calls the callback.
        /// </summary>
        /// <param name="context">The context: a <see cref="FullRuleCallbackContext"/> when <see cref="Full"/>.</param>
        public void Invoke(RuleCallbackContext context)
        {
            if (full is not null)
                full((FullRuleCallbackContext)context);
            else
                write!(context);
        }

    }

}
