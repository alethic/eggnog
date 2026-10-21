using System.Collections.Generic;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// egglog's <c>DelayStopScheduler</c> (<c>src/scheduler.rs</c>, its tests): chooses nothing, never asks for new
    /// matches, and says it can stop only from its second call of <see cref="CanStop"/> on; each clone counts its own
    /// calls.
    /// </summary>
    public sealed class DelayStopScheduler : IRuleScheduler
    {

        /// <summary>
        /// Gets how many times <see cref="CanStop"/> was called on this instance.
        /// </summary>
        public int CanStopCalls { get; private set; }

        /// <inheritdoc />
        public bool CanStop(IReadOnlyList<string> rules, string ruleset)
        {
            CanStopCalls++;
            return CanStopCalls > 1;
        }

        /// <inheritdoc />
        public bool FilterMatches(string rule, string ruleset, RuleMatches matches) => false;

        /// <inheritdoc />
        public IRuleScheduler Clone() => new DelayStopScheduler { CanStopCalls = CanStopCalls };

    }

}
