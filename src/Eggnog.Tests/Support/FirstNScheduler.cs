using System.Collections.Generic;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// egglog's <c>FirstNScheduler</c> (<c>src/scheduler.rs</c>, its tests): fires every match when there are at most
    /// <c>n</c>, else the first <c>n</c>, and asks for new matches once fewer than <c>2n</c> are offered. Logs, across its
    /// clones, how many matches each call was offered, and the first value of each, for the tests to read.
    /// </summary>
    public sealed class FirstNScheduler : IRuleScheduler
    {

        readonly int n;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="n">The most matches a step fires.</param>
        public FirstNScheduler(int n)
            : this(n, new List<int>(), new List<object?>())
        {

        }

        FirstNScheduler(int n, List<int> offered, List<object?> firsts)
        {
            this.n = n;
            Offered = offered;
            Firsts = firsts;
        }

        /// <summary>
        /// Gets how many matches each call was offered, in call order, shared by the clones.
        /// </summary>
        public List<int> Offered { get; }

        /// <summary>
        /// Gets the value of the first variable of the first match each call was offered, <c>null</c> for none.
        /// </summary>
        public List<object?> Firsts { get; }

        /// <inheritdoc />
        public bool FilterMatches(string rule, string ruleset, RuleMatches matches)
        {
            Offered.Add(matches.Count);
            Firsts.Add(matches.Count > 0 ? matches[0].Get(matches.Variables[0]) : null);
            if (matches.Count <= n)
            {
                matches.ChooseAll();
            }
            else
            {
                for (var i = 0; i < n; i++)
                    matches.Choose(i);
            }

            return matches.Count < n * 2;
        }

        /// <inheritdoc />
        public IRuleScheduler Clone() => new FirstNScheduler(n, Offered, Firsts);

    }

}
