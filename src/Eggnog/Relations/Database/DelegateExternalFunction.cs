using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="ExternalFunction"/> that forwards to a callback: the <c>Wrapped&lt;F&gt;</c> of egglog's
    /// <c>make_external_func</c> (core-relations/src/free_join/mod.rs:196-213). Created by
    /// <see cref="ExternalFunction.Create(ExternalFunctionCallback, bool)"/>. One delegate call per invocation on top of the virtual call.
    /// </summary>
    internal sealed class DelegateExternalFunction : ExternalFunction
    {

        readonly ExternalFunctionCallback callback;
        readonly bool readsTables;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="callback">The implementation.</param>
        /// <param name="readsTables">Whether the callback reads table contents (<see cref="ExternalFunction.ReadsTables"/>):
        /// the bridge sets it for primitives that read tables, which keeps their rules from inheriting watermarks
        /// (colored-contexts spec §10.2).</param>
        public DelegateExternalFunction(ExternalFunctionCallback callback, bool readsTables = false)
        {
            ArgumentNullException.ThrowIfNull(callback);
            this.callback = callback;
            this.readsTables = readsTables;
        }

        /// <inheritdoc />
        public override bool ReadsTables => readsTables;

        /// <inheritdoc />
        public override bool TryInvoke(ExecutionState state, ReadOnlySpan<uint> args, out uint result) => callback(state, args, out result);

    }

}
