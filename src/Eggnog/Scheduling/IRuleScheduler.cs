using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A scheduler: it decides which of a rule's matches fire in a step of <see cref="Schedule.RunWith"/>, and the
    /// matches it does not choose wait for the next step. egglog's <c>Scheduler</c> (<c>src/scheduler.rs</c>), added to a
    /// graph with <see cref="EGraph.AddScheduler"/>.
    /// </summary>
    /// <remarks>
    /// The instance given to <see cref="EGraph.AddScheduler"/> is a prototype that is never called: each scope that steps
    /// the scheduler steps its own <see cref="Clone"/>, and a child starts from a clone of its parent's, as egglog's
    /// <c>push</c> clones the scheduler with the e-graph. A scheduler is called on the thread that runs the scope, and
    /// <see cref="Clone"/> may be called on several threads at once: children of a frozen scope, each on its own thread,
    /// clone the same state of their parent's, or the prototype.
    /// </remarks>
    public interface IRuleScheduler
    {

        /// <summary>
        /// Gets whether a step that changed nothing may be taken as the end: egglog's <c>can_stop</c>, which a
        /// <see cref="Schedule.Repeat"/> stops on. Called only after a step that changed nothing; <c>true</c> by default.
        /// </summary>
        /// <param name="rules">The names of the ruleset's rules.</param>
        /// <param name="ruleset">The ruleset's name.</param>
        /// <returns>Whether nothing is left that another step would do.</returns>
        bool CanStop(IReadOnlyList<string> rules, string ruleset) => true;

        /// <summary>
        /// Chooses which of a rule's matches fire: egglog's <c>filter_matches</c>. The matches are those it left
        /// before, then the new ones the rule's query found; those not chosen wait for the next step.
        /// </summary>
        /// <param name="rule">The rule's name.</param>
        /// <param name="ruleset">The ruleset's name.</param>
        /// <param name="matches">The matches, to choose from.</param>
        /// <returns>Whether the rule's query runs at the next step, to offer new matches; when it does not, only those
        /// left are offered.</returns>
        bool FilterMatches(string rule, string ruleset, RuleMatches matches);

        /// <summary>
        /// Copies the scheduler with its state, for a scope that steps it: egglog's scheduler is cloned with the e-graph.
        /// </summary>
        /// <returns>The copy.</returns>
        IRuleScheduler Clone();

    }

}
