using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The shape of an <see cref="ExternalFunction"/> written as a lambda: egglog's
    /// <c>Fn(&amp;mut ExecutionState, &amp;[Value]) -&gt; Option&lt;Value&gt;</c> taken by <c>make_external_func</c>.
    /// </summary>
    /// <param name="state">The calling execution state.</param>
    /// <param name="args">The arguments.</param>
    /// <param name="result">The value produced, when the result is <c>true</c>.</param>
    /// <returns><c>false</c> to produce no value and halt the current rule lane.</returns>
    internal delegate bool ExternalFunctionCallback(ExecutionState state, ReadOnlySpan<uint> args, out uint result);

}
