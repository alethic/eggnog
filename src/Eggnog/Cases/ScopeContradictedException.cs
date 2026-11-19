using System;

namespace Eggnog
{

    /// <summary>
    /// A read or a write that needs a model, made on a scope whose contradiction stopped it. A stopped scope has no
    /// model: it answers <see cref="Scope.IsContradictory"/> (<c>true</c>) and its identity members, does nothing on
    /// <see cref="Scope.Assume"/>, <see cref="Scope.Subsume{TSort}"/> or <see cref="Scope.Delete{T}"/>, nor on their
    /// overloads over rows, returns <c>false</c> from <see cref="Scope.Union{TSort}"/>, returns at once from
    /// <see cref="Scope.Run"/> with <see cref="RunStopReason.Contradiction"/>, and may be disposed; every other member
    /// throws this. A scope under a stopped scope has no model either, and answers as the stopped scope does. No scope
    /// the library makes stops, so no call into the library throws this: a contradictory scope stays fully usable, and a
    /// run that stops on contradiction (<see cref="RunOptions.StopOnContradiction"/>) stops itself, not its scope.
    /// </summary>
    public sealed class ScopeContradictedException : InvalidOperationException
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scopePath">The path of the stopped scope.</param>
        /// <exception cref="ArgumentNullException"><paramref name="scopePath"/> is <c>null</c>.</exception>
        public ScopeContradictedException(string scopePath)
            : base($"Scope '{scopePath ?? throw new ArgumentNullException(nameof(scopePath))}' is stopped by its contradiction: it has no model, so reads and writes that need one are refused.")
        {
            ScopePath = scopePath;
        }

        /// <summary>
        /// Initializes a new instance for a call on a scope under a stopped scope.
        /// </summary>
        /// <param name="scopePath">The path of the scope the call was made on.</param>
        /// <param name="stoppedCasePath">The path of the stopped scope it lies under.</param>
        internal ScopeContradictedException(string scopePath, string stoppedCasePath)
            : base($"Scope '{scopePath}' lies under '{stoppedCasePath}', which its contradiction stopped: it has no model either, so reads and writes that need one are refused.")
        {
            ScopePath = scopePath;
        }

        /// <summary>
        /// Gets the path of the scope the call was made on: the stopped scope, or a scope under one.
        /// </summary>
        public string ScopePath { get; }

    }

}
