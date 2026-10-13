using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// An egglog-level panic raised while an <see cref="EGraph"/> ran rules or rebuilt: a panic function was called (a
    /// failed lookup of a custom function, a conflict in a function without a merge, a failed primitive, an explicit
    /// <c>panic</c>). egglog-bridge's <c>PanicError</c> (egglog-bridge/src/lib.rs:1620-1622), which <c>run_rules</c>
    /// returns as an <c>Err</c>.
    /// </summary>
    /// <remarks>
    /// The run is not rolled back: the writes it made before stopping, and their merge, stay, as in egglog.
    /// </remarks>
    internal sealed class PanicException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="panicMessage">The message the panic function recorded.</param>
        public PanicException(string panicMessage)
            : base("Panic: " + panicMessage)
        {
            PanicMessage = panicMessage;
        }

        /// <summary>
        /// Gets the message the panic function recorded.
        /// </summary>
        public string PanicMessage { get; }

    }

}
