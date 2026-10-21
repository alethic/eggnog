using System;
using System.Collections.Generic;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A scheduler over delegates, for tests: its clones share them.
    /// </summary>
    public sealed class DelegateScheduler : IRuleScheduler
    {

        readonly Func<string, RuleMatches, bool> filter;
        readonly Func<bool>? canStop;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="filter">The filter, given the rule's name and its matches.</param>
        /// <param name="canStop">Whether it can stop; <c>null</c> for always.</param>
        public DelegateScheduler(Func<string, RuleMatches, bool> filter, Func<bool>? canStop = null)
        {
            this.filter = filter;
            this.canStop = canStop;
        }

        /// <inheritdoc />
        public bool CanStop(IReadOnlyList<string> rules, string ruleset) => canStop?.Invoke() ?? true;

        /// <inheritdoc />
        public bool FilterMatches(string rule, string ruleset, RuleMatches matches) => filter(rule, matches);

        /// <inheritdoc />
        public IRuleScheduler Clone() => new DelegateScheduler(filter, canStop);

    }

}
