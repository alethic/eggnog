using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Thrown by <see cref="BridgeDiffRunner"/> when two evaluations of a bridge differential program disagree: different
    /// classes or rows modulo renaming, a non-canonical or duplicate row, or different change flags.
    /// </summary>
    sealed class BridgeDiffMismatchException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">What disagreed, where, and both sides.</param>
        public BridgeDiffMismatchException(string message)
            : base(message)
        {

        }

    }

}
