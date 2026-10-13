using System;

namespace Eggnog
{

    /// <summary>
    /// A read or a write that needs a model, made on a case whose contradiction stopped it. A stopped case has no
    /// model: it answers <see cref="Scope.IsContradictory"/> (<c>true</c>) and its identity members, does nothing on
    /// <see cref="Scope.Assume"/>, returns <c>false</c> from <see cref="Scope.Union{TSort}"/>, returns at once from
    /// <see cref="Scope.Run"/> with <see cref="RunStopReason.Contradiction"/>, and may be disposed; every other member
    /// throws this. Only a case can stop; any other contradictory scope stays fully usable, unless it lies under a
    /// stopped case: such a scope has no model either, and answers as the stopped case does.
    /// </summary>
    public sealed class ScopeContradictedException : InvalidOperationException
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scopePath">The path of the stopped case.</param>
        /// <exception cref="ArgumentNullException"><paramref name="scopePath"/> is <c>null</c>.</exception>
        public ScopeContradictedException(string scopePath)
            : base($"Scope '{scopePath ?? throw new ArgumentNullException(nameof(scopePath))}' is a contradicted case: it has no model, so reads and writes that need one are refused.")
        {
            ScopePath = scopePath;
        }

        /// <summary>
        /// Initializes a new instance for a call on a scope under a stopped case.
        /// </summary>
        /// <param name="scopePath">The path of the scope the call was made on.</param>
        /// <param name="stoppedCasePath">The path of the stopped case it lies under.</param>
        internal ScopeContradictedException(string scopePath, string stoppedCasePath)
            : base($"Scope '{scopePath}' lies under '{stoppedCasePath}', a contradicted case: it has no model either, so reads and writes that need one are refused.")
        {
            ScopePath = scopePath;
        }

        /// <summary>
        /// Gets the path of the scope the call was made on: the stopped case, or a scope under one.
        /// </summary>
        public string ScopePath { get; }

    }

}
