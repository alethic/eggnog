using System;

namespace Eggnog
{

    /// <summary>
    /// A container of classes that a frozen scope computed and that its view does not hold: a frozen scope, the root or
    /// a child, is read-only, and so is a query on one, which runs in a transient child but interns for the frozen
    /// scope. It names no row and equals no cell, so a read that meets one finds nothing, and a write that needs it is
    /// refused (decisions row 9).
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
