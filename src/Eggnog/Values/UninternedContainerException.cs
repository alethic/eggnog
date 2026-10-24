using System;

namespace Eggnog
{

    /// <summary>
    /// A container of classes that a scope which cannot intern one computed, and that the scope's view does not hold:
    /// in a child, or in a read of a frozen root. It names no row and equals no cell, so a read that meets one finds
    /// nothing, and a write that needs it is refused (decisions row 9).
    /// </summary>
    internal sealed class UninternedContainerException : InvalidOperationException
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">The message.</param>
        public UninternedContainerException(string message)
            : base(message)
        {
        }

    }

}
