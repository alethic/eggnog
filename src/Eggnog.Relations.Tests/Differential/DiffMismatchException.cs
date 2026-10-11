using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Thrown by <see cref="DiffRunner"/> when the engine disagrees with the model: different rows, matches,
    /// multiplicities, match counts or change flags.
    /// </summary>
    sealed class DiffMismatchException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">What disagreed, with the expected and actual values.</param>
        public DiffMismatchException(string message)
            : base(message)
        {

        }

    }

}
