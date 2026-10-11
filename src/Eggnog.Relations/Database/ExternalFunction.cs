using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A host-provided operation that rules and merges can call with access to the database: egglog's
    /// <c>ExternalFunction</c> trait (core-relations/src/free_join/mod.rs:185-194). Primitives, panics and side
    /// channels are external functions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryInvoke(ExecutionState, ReadOnlySpan{uint}, out uint)"/> receives the calling execution state,
    /// through which it may stage writes, read tables, advance counters and call other external functions
    /// (<see cref="ExecutionState.View"/>). Returning <c>false</c> produces no value, which halts the current rule
    /// lane (egglog's <c>None</c>).
    /// </para>
    /// <para>
    /// egglog requires <c>Send + Sync</c>: a function is called from many threads at once, each with its own state, so
    /// it must not keep unsynchronized mutable state. egglog clones boxed functions when the database is cloned; here
    /// a cloned database shares the instances, which is the same for any function whose captured state is shared
    /// (egglog's <c>Arc</c>) or immutable.
    /// </para>
    /// <para>
    /// One virtual call per invocation, as egglog's <c>dyn ExternalFunction</c>; the actions milestone batches calls
    /// per binding set. Compiled plans call the array overload, which cannot take a span.
    /// </para>
    /// </remarks>
    internal abstract class ExternalFunction
    {

        /// <summary>
        /// Creates an external function from a callback: egglog's <c>make_external_func</c>
        /// (core-relations/src/free_join/mod.rs:196-213).
        /// </summary>
        /// <param name="callback">The implementation.</param>
        /// <returns>The function.</returns>
        public static ExternalFunction Create(ExternalFunctionCallback callback) => new DelegateExternalFunction(callback);

        /// <summary>
        /// Invokes the function. egglog's <c>invoke</c>.
        /// </summary>
        /// <param name="state">The calling execution state.</param>
        /// <param name="args">The arguments.</param>
        /// <param name="result">The value produced, when the result is <c>true</c>.</param>
        /// <returns><c>false</c> to produce no value and halt the current rule lane.</returns>
        public abstract bool TryInvoke(ExecutionState state, ReadOnlySpan<uint> args, out uint result);

        /// <summary>
        /// Invokes the function on the arguments <paramref name="args"/>[<paramref name="offset"/>..+<paramref name="count"/>].
        /// The array form for compiled plans.
        /// </summary>
        /// <param name="state">The calling execution state.</param>
        /// <param name="args">The array holding the arguments.</param>
        /// <param name="offset">The index of the first argument.</param>
        /// <param name="count">The number of arguments.</param>
        /// <param name="result">The value produced, when the result is <c>true</c>.</param>
        /// <returns><c>false</c> to produce no value.</returns>
        public bool TryInvoke(ExecutionState state, uint[] args, int offset, int count, out uint result)
        {
            return TryInvoke(state, new ReadOnlySpan<uint>(args, offset, count), out result);
        }

    }

}
