using System.Linq;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A rule or a query of a <see cref="ScopedProgram"/>: a body of atoms, an optional filter <c>var &lt; bound</c> on
    /// an <c>i64</c> variable, and a head of atoms (empty for a query, whose variables are its body's).
    /// </summary>
    /// <param name="Name">The rule's name, unique in the program.</param>
    /// <param name="Body">The body's atoms.</param>
    /// <param name="FilterVar">The filtered variable; <c>null</c> for no filter.</param>
    /// <param name="FilterBelow">The filter's exclusive bound.</param>
    /// <param name="Head">The head's atoms: rows written in the running scope.</param>
    internal sealed record ScopedRuleSpec(string Name, ScopedAtomSpec[] Body, string? FilterVar, long FilterBelow, ScopedAtomSpec[] Head)
    {

        /// <summary>
        /// Gets the body's variables in first-use order: the columns of a query's matches.
        /// </summary>
        public string[] Variables =>
            Body.SelectMany(a => a.Keys.Append(a.Output)).Where(t => t?.Var is not null).Select(t => t!.Var!).Distinct().ToArray();

        /// <summary>
        /// Gets the tags the body reads, distinct.
        /// </summary>
        public int[] Tags => Body.Where(a => a.Tag >= 0).Select(a => a.Tag).Distinct().ToArray();

        /// <inheritdoc />
        public override string ToString() =>
            $"{Name}: {string.Join(", ", Body.Select(a => a.ToString()))}{(FilterVar is null ? "" : $", {FilterVar} < {FilterBelow}")} -> {string.Join(", ", Head.Select(a => a.ToString()))}";

    }

}
