using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Thrown by <see cref="EGraphContext.CheckInvariants"/> when a context violates an invariant: the debug checker
    /// that tests run after every rebuild, rule run and flush. Lists every violation found.
    /// </summary>
    /// <remarks>
    /// An <see cref="InvalidOperationException"/>, so callers that treat any checker failure as a broken context need not
    /// know the type. Each violation starts with the name of the invariant or part it breaks, as the storage checker
    /// names the storage parts and the bridge checker the rest (for example <c>journal:</c> or
    /// <c>rule progress:</c>).
    /// </remarks>
    internal sealed class ContextInvariantException : InvalidOperationException
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="depth">The depth of the checked context.</param>
        /// <param name="violations">One line per violation; copied.</param>
        public ContextInvariantException(int depth, IReadOnlyList<string> violations)
            : base(Format(depth, violations))
        {
            Violations = [.. violations];
        }

        /// <summary>
        /// Gets the violations, one line each.
        /// </summary>
        public IReadOnlyList<string> Violations { get; }

        /// <summary>
        /// Builds the message: the context's depth, the number of violations, then one line per violation.
        /// </summary>
        static string Format(int depth, IReadOnlyList<string> violations)
        {
            ArgumentNullException.ThrowIfNull(violations);
            return $"The context at depth {depth} violates {violations.Count} invariant(s):{Environment.NewLine}{string.Join(Environment.NewLine, violations)}";
        }

    }

}
