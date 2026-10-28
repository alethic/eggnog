using System;

namespace Eggnog
{

    /// <summary>
    /// The rules every definition name obeys. Uniqueness is checked by the caller, under the graph's definition lock.
    /// </summary>
    internal static class NameRules
    {

        /// <summary>
        /// Checks a name of a table, sort, value type, ruleset, rule or variable: not <c>null</c> and not empty. As in
        /// egglog, a table's name may hold any character, <c>'@'</c> and a leading <c>'$'</c> included: the tables the
        /// library makes for itself (<see cref="EGraph.Contradiction"/>, mirrors and worklists) take no name of the user's.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="parameter">The parameter name for exceptions.</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The name is empty.</exception>
        public static void ValidateName(string name, string parameter)
        {
            ArgumentNullException.ThrowIfNull(name, parameter);
            if (name.Length == 0)
                throw new ArgumentException("A name must not be empty.", parameter);
        }

    }

}
