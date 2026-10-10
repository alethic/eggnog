using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Thrown in place of a failed <see cref="System.Diagnostics.Debug.Assert(bool)"/> while a
    /// <see cref="DebugAssertScope"/> is active, so tests can observe what egglog checks with <c>debug_assert!</c>.
    /// </summary>
    sealed class DebugAssertFailedException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">The assertion message.</param>
        public DebugAssertFailedException(string? message) :
            base(message)
        {

        }

    }

}
