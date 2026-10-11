using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// An external function that raises an egglog-level panic: it requests early stopping and records its message in a
    /// side channel unless one is already there, and produces no value. egglog-bridge's <c>Panic</c>
    /// (egglog-bridge/src/lib.rs:1474-1479, 1503-1515). The e-graph interns one per message
    /// (<see cref="EGraph.NewPanic"/>) and turns a recorded message into a <see cref="PanicException"/> after the run.
    /// </summary>
    /// <remarks>
    /// Stateless apart from the shared channel; safe to call from parallel tasks.
    /// </remarks>
    internal sealed class PanicFunction : ExternalFunction
    {

        readonly SideChannel<string> channel;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">The panic message.</param>
        /// <param name="channel">The channel the message is recorded in.</param>
        public PanicFunction(string message, SideChannel<string> channel)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(channel);
            Message = message;
            this.channel = channel;
        }

        /// <summary>
        /// Gets the panic message.
        /// </summary>
        public string Message { get; }

        /// <inheritdoc />
        /// <exception cref="ArgumentException">Arguments were passed; a panic takes none (egglog asserts it).</exception>
        public override bool TryInvoke(ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (args.IsEmpty == false)
                throw new ArgumentException("A panic function takes no arguments.", nameof(args));

            state.TriggerEarlyStop();
            channel.TrySet(Message);
            result = 0;
            return false;
        }

    }

}
