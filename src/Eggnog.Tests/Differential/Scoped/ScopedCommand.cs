using System;
using System.Globalization;
using System.Linq;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// One step of a <see cref="ScopedProgram"/>. Scopes are numbered in creation order; 0 is the root.
    /// </summary>
    /// <param name="Kind">What the step does.</param>
    /// <param name="Scope">The scope it acts on: the new scope's number for <see cref="ScopedCommandKind.Create"/>.</param>
    /// <param name="Parent">The parent of a new scope.</param>
    /// <param name="Tag">The tag of a new scope; -1 for none.</param>
    /// <param name="Facts">The ground facts of an <see cref="ScopedCommandKind.Assume"/>.</param>
    /// <param name="Rules">The local rules of a new scope.</param>
    internal sealed record ScopedCommand(ScopedCommandKind Kind, int Scope, int Parent, int Tag, ScopedAtomSpec[] Facts, ScopedRuleSpec[] Rules)
    {

        /// <summary>
        /// Makes a command with no facts and no rules.
        /// </summary>
        /// <param name="kind">The kind.</param>
        /// <param name="scope">The scope.</param>
        /// <returns>The command.</returns>
        public static ScopedCommand Of(ScopedCommandKind kind, int scope) => new(kind, scope, -1, -1, Array.Empty<ScopedAtomSpec>(), Array.Empty<ScopedRuleSpec>());

        /// <inheritdoc />
        public override string ToString() => Kind switch
        {
            ScopedCommandKind.Create => $"create s{Scope} under s{Parent}{(Tag >= 0 ? " tagged t" + Tag.ToString(CultureInfo.InvariantCulture) : "")}"
                + string.Concat(Rules.Select(r => "\n    local " + r)),
            ScopedCommandKind.Assume => $"assume s{Scope}: {string.Join(", ", Facts.Select(f => f.ToString()))}",
            _ => $"{Kind.ToString().ToLowerInvariant()} s{Scope}",
        };

    }

}
