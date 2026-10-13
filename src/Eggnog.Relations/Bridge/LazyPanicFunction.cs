using System;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A <see cref="PanicFunction"/> whose message is built only when it first panics: egglog-bridge's <c>LazyPanic</c>
    /// (egglog-bridge/src/lib.rs:1439-1472). Rules use it for failure messages that are rarely needed; unlike eager
    /// panics these are not interned by message, since there is no key to intern them by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The message factory runs at most once, whichever thread panics first (egglog's <c>once_cell::Lazy</c>), and only
    /// when no failure is recorded yet where the message would go: egglog forces it only when its channel is empty
    /// (lib.rs:1460-1463).
    /// </para>
    /// <para>
    /// As <see cref="PanicFunction"/>: the failure is the calling state's (per context, colored-contexts spec §10.6),
    /// and the optional root side channel is written only for root states.
    /// </para>
    /// </remarks>
    internal sealed class LazyPanicFunction : ExternalFunction
    {

        readonly Lazy<string> message;
        readonly SideChannel<string>? channel;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">Builds the panic message.</param>
        /// <param name="channel">The root side channel the message is also recorded in for root states, or
        /// <c>null</c>.</param>
        public LazyPanicFunction(Func<string> message, SideChannel<string>? channel)
        {
            ArgumentNullException.ThrowIfNull(message);
            this.message = new Lazy<string>(message, LazyThreadSafetyMode.ExecutionAndPublication);
            this.channel = channel;
        }

        /// <summary>
        /// Gets whether the message has been built.
        /// </summary>
        public bool IsMessageCreated => message.IsValueCreated;

        /// <inheritdoc />
        /// <exception cref="ArgumentException">Arguments were passed; a panic takes none (egglog asserts it).</exception>
        public override bool TryInvoke(ExecutionState state, ReadOnlySpan<uint> args, out uint result)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (args.IsEmpty == false)
                throw new ArgumentException("A panic function takes no arguments.", nameof(args));

            state.TriggerEarlyStop();
            var root = channel is not null && state.InChildContext == false;

            // egglog forces the message only when the channel is empty (lib.rs:1460-1463); the state's failure is the
            // per-context channel, and the root side channel the e-graph's.
            if (state.Failure is null || (root && channel!.HasValue == false))
            {
                var text = message.Value;
                state.ReportFailure(text);
                if (root)
                    channel!.TrySet(text);
            }

            result = 0;
            return false;
        }

    }

}
