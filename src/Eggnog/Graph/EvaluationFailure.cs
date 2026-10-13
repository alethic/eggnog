using System;

namespace Eggnog
{

    /// <summary>
    /// An evaluation failure an external function recorded during an engine call: which rule, what failed, the
    /// decoded arguments, and the exception. The rule layer turns the first one of a call into a
    /// <see cref="RuleEvaluationException"/>.
    /// </summary>
    /// <param name="RuleName">The rule whose call site failed; <c>null</c> for a lattice merge, which no rule owns.</param>
    /// <param name="What">What failed, as the exception's message.</param>
    /// <param name="Bindings">The decoded arguments with their source names, e.g. <c>d={a, b} v="b"</c>; <c>null</c>
    /// for merges.</param>
    /// <param name="Exception">The exception the user delegate threw.</param>
    internal sealed record EvaluationFailure(string? RuleName, string What, string? Bindings, Exception? Exception);

}
