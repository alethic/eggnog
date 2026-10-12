using System;

namespace Eggnog
{

    /// <summary>
    /// The rules every definition name obeys. Uniqueness is checked by the caller, under the graph's definition lock.
    /// </summary>
    internal static class NameRules
    {

        /// <summary>
        /// Checks a table name: a valid name (<see cref="ValidateName"/>) that contains no <c>'@'</c> (reserved for scoped
        /// views, <c>dom@schema</c>) and does not start with <c>'$'</c> (reserved for internal tables).
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="parameter">The parameter name for exceptions.</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The name is empty, contains <c>'@'</c> or starts with
        /// <c>'$'</c>.</exception>
        public static void ValidateTableName(string name, string parameter)
        {
            ValidateName(name, parameter);
            if (name.Contains('@', StringComparison.Ordinal))
                throw new ArgumentException($"The table name '{name}' contains '@', which names scoped views.", parameter);

            if (name[0] == '$')
                throw new ArgumentException($"The table name '{name}' starts with '$', which is reserved for internal tables.", parameter);
        }

        /// <summary>
        /// Checks a name of a sort, value type, ruleset, rule or variable: not <c>null</c> and not empty.
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
