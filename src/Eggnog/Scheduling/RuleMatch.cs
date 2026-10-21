using System;

namespace Eggnog
{

    /// <summary>
    /// One of a rule's matches (<see cref="RuleMatches"/>): egglog's <c>Match</c>, the values of the variables the rule's
    /// head uses. Valid only during <see cref="IRuleScheduler.FilterMatches"/>.
    /// </summary>
    public readonly struct RuleMatch
    {

        readonly RuleMatches matches;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="matches">The matches it is one of.</param>
        /// <param name="index">Its index.</param>
        internal RuleMatch(RuleMatches matches, int index)
        {
            this.matches = matches;
            Index = index;
        }

        /// <summary>
        /// Gets the match's index among the rule's matches, which <see cref="RuleMatches.Choose"/> takes.
        /// </summary>
        public int Index { get; }

        /// <summary>
        /// Gets the value of a variable: egglog's <c>get_value</c>. A class is a sort value of the scope that steps.
        /// </summary>
        /// <param name="variable">The variable's name, one of <see cref="RuleMatches.Variables"/>.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">No variable of the match has the name.</exception>
        /// <exception cref="InvalidOperationException">The match is <c>default</c>.</exception>
        public object Get(string variable) =>
            (matches ?? throw new InvalidOperationException("A default RuleMatch has no values.")).Get(Index, variable);

        /// <summary>
        /// Gets the value of a variable as <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The variable's type.</typeparam>
        /// <param name="variable">The variable's name, one of <see cref="RuleMatches.Variables"/>.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">No variable of the match has the name.</exception>
        /// <exception cref="InvalidCastException">The variable is not a <typeparamref name="T"/>.</exception>
        /// <exception cref="InvalidOperationException">The match is <c>default</c>.</exception>
        public T Get<T>(string variable) => (T)Get(variable);

    }

}
