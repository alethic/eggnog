using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="ExternalFunction"/> that forwards to a callback: the <c>Wrapped&lt;F&gt;</c> of egglog's
    /// <c>make_external_func</c> (core-relations/src/free_join/mod.rs:196-213). Created by
    /// <see cref="ExternalFunction.Create"/>. One delegate call per invocation on top of the virtual call.
    /// </summary>
    internal sealed class DelegateExternalFunction : ExternalFunction
    {

        readonly ExternalFunctionCallback callback;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="callback">The implementation.</param>
        public DelegateExternalFunction(ExternalFunctionCallback callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            this.callback = callback;
        }

        /// <inheritdoc />
        public override bool TryInvoke(ExecutionState state, ReadOnlySpan<uint> args, out uint result) => callback(state, args, out result);

    }

}
