using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    public sealed partial class EGraph
    {

        /// <summary>
        /// The panic side channel every panic function of this e-graph records its message in (lib.rs:122).
        /// </summary>
        readonly SideChannel<string> panicMessage = new();

        /// <summary>
        /// The eager panic functions, by message, so that repeated messages share one external function (lib.rs:123-128).
        /// </summary>
        readonly Dictionary<string, ExternalFunctionId> panicFuncs = new(StringComparer.Ordinal);

        /// <summary>
        /// The inverse of <see cref="panicFuncs"/>: the message of each interned eager panic function, by id, so that
        /// <see cref="FreeExternalFunction"/> forgets a freed panic in constant time.
        /// </summary>
        readonly DenseIdMap<ExternalFunctionId, string> panicFuncMessages = new();

        /// <summary>
        /// Gets the panic side channel: the first message recorded by a panic function since the last run reported one.
        /// </summary>
        internal SideChannel<string> PanicChannel => panicMessage;

        /// <summary>
        /// Gets the number of interned eager panic functions.
        /// </summary>
        internal int PanicFunctionCount => panicFuncs.Count;

        /// <summary>
        /// Gets the external function that panics with <paramref name="message"/>, creating it the first time the message
        /// is used. egglog's <c>new_panic</c> (lib.rs:1482-1491).
        /// </summary>
        /// <param name="message">The panic message.</param>
        /// <returns>The function's id; the same for the same message.</returns>
        public ExternalFunctionId NewPanic(string message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (panicFuncs.TryGetValue(message, out var id))
                return id;

            id = db.AddExternalFunction(new PanicFunction(message, panicMessage));
            panicFuncs.Add(message, id);
            panicFuncMessages.Insert(id, message);
            return id;
        }

        /// <summary>
        /// Creates an external function that panics with the message <paramref name="message"/> builds, which runs only
        /// when it first panics. egglog's <c>new_panic_lazy</c> (lib.rs:1493-1500). Not interned: every call creates a
        /// function.
        /// </summary>
        /// <param name="message">Builds the panic message.</param>
        /// <returns>The function's id.</returns>
        public ExternalFunctionId NewPanicLazy(Func<string> message)
        {
            ArgumentNullException.ThrowIfNull(message);
            return db.AddExternalFunction(new LazyPanicFunction(message, panicMessage));
        }

        /// <summary>
        /// Registers a low-level external function, which receives the raw execution state. egglog's
        /// <c>register_external_func</c> (lib.rs:275-291): a trust boundary, since the function has unrestricted access.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>Its id; freed ids are reused.</returns>
        internal ExternalFunctionId RegisterExternalFunction(ExternalFunction function)
        {
            ArgumentNullException.ThrowIfNull(function);
            return db.AddExternalFunction(function);
        }

        /// <summary>
        /// Unregisters an external function; its id may be handed out again. egglog's <c>free_external_func</c>.
        /// </summary>
        /// <remarks>
        /// Defect fixed (egglog-bridge/src/lib.rs:293-295, 1482-1491; map §8.1 row 24): egglog leaves a freed panic
        /// function in its message cache, so a later <see cref="NewPanic"/> with that message would return an id that may
        /// by then belong to an unrelated function. Here freeing a panic function also forgets its message, in constant
        /// time.
        /// </remarks>
        /// <param name="function">The function's id.</param>
        public void FreeExternalFunction(ExternalFunctionId function)
        {
            db.FreeExternalFunction(function);
            if (panicFuncMessages.TryTake(function, out var message))
                panicFuncs.Remove(message);
        }

        /// <summary>
        /// Takes the first panic recorded since the last call, if any: the panic side channel's message, or else a failure
        /// the database collected from a state through <see cref="ExecutionState.ReportFailure"/> (the engine's own
        /// failure channel, which core merge functions and external functions may use).
        /// </summary>
        /// <param name="message">The message.</param>
        /// <returns><c>true</c> if a panic was recorded.</returns>
        internal bool TryTakePanic(out string message)
        {
            if (panicMessage.TryTake(out var recorded))
            {
                // A failure reported through the engine's channel in the same run is the same event or a later one.
                db.TakeFailure();
                message = recorded;
                return true;
            }

            var failure = db.TakeFailure();
            message = failure ?? string.Empty;
            return failure is not null;
        }

        /// <summary>
        /// Throws the recorded panic, if any: what egglog's <c>run_rules</c> does after the rule set and after the
        /// rebuild (lib.rs:597-599, 619-621).
        /// </summary>
        /// <exception cref="PanicException">A panic was recorded.</exception>
        void ThrowIfPanicked()
        {
            if (TryTakePanic(out var message))
                throw new PanicException(message);
        }

    }

}
