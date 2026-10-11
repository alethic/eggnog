using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Thrown by the bridge differential oracle (<see cref="BridgeDiffOracle"/>) where the bridge panics
    /// (<see cref="PanicException"/>): a right-hand-side read of a function without a default at a key it does not hold,
    /// or a right-hand-side primitive that produces nothing. The oracle is unusable afterwards.
    /// </summary>
    sealed class BridgeDiffPanicException : InvalidOperationException
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">What failed.</param>
        public BridgeDiffPanicException(string message)
            : base(message)
        {
        }

    }

}
