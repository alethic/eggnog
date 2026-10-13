using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// An external function that raises an egglog-level panic: it reports its message as the calling state's failure
    /// (<see cref="ExecutionState.ReportFailure"/>, which also requests early stopping) and produces no value.
    /// egglog-bridge's <c>Panic</c> (egglog-bridge/src/lib.rs:1474-1479, 1503-1515). The e-graph interns one per message
    /// (<see cref="EGraph.NewPanic"/>) and turns a recorded failure into a <see cref="PanicException"/> after the run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failures stay per context (colored-contexts spec §10.6): the state belongs to the context whose run, merge or flush
    /// called the function, so the failure lands in that context's database (<see cref="Database.Failure"/>) and is
    /// reported by that context alone. egglog records the message in a side channel shared by the whole e-graph instead.
    /// </para>
    /// <para>
    /// For the e-graph's egglog-compatible root API, the function also records its message in the e-graph's side channel
    /// (<see cref="EGraph.PanicChannel"/>), as egglog does, but only for a state of a root or bare database
    /// (<see cref="ExecutionState.InChildContext"/> is <c>false</c>): a child's panic never reaches the root. The channel
    /// is optional (<c>null</c> records nothing there).
    /// </para>
    /// <para>
    /// Stateless apart from the shared channel; safe to call from parallel tasks.
    /// </para>
    /// </remarks>
    internal sealed class PanicFunction : ExternalFunction
    {

        readonly SideChannel<string>? channel;

        /// <summary>
        /// Initializes a new instance that reports through the calling state only.
        /// </summary>
        /// <param name="message">The panic message.</param>
        public PanicFunction(string message)
            : this(message, null)
        {

        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">The panic message.</param>
        /// <param name="channel">The root side channel the message is also recorded in for root states, or
        /// <c>null</c>.</param>
        public PanicFunction(string message, SideChannel<string>? channel)
        {
            ArgumentNullException.ThrowIfNull(message);
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

            state.ReportFailure(Message);
            if (channel is not null && state.InChildContext == false)
                channel.TrySet(Message);

            result = 0;
            return false;
        }

    }

}
