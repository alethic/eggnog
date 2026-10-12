using System;

namespace Eggnog
{

    /// <summary>
    /// An evaluation failure: a primitive, filter or lattice delegate threw, or a function without a lattice received two
    /// values from rules. After a run or a write the scope is faulted (D18).
    /// </summary>
    public sealed class RuleEvaluationException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="ruleName">The rule whose call site failed; <c>null</c> for a merge.</param>
        /// <param name="bindings">The failed call's decoded arguments; <c>null</c> for a merge.</param>
        /// <param name="message">What failed.</param>
        /// <param name="inner">The exception that caused it.</param>
        public RuleEvaluationException(string? ruleName, string? bindings, string message, Exception? inner)
            : base(message, inner)
        {
            RuleName = ruleName;
            Bindings = bindings;
        }

        /// <summary>
        /// Gets the rule whose call site failed; <c>null</c> when the failure happened in a merge, which no single rule
        /// owns.
        /// </summary>
        public string? RuleName { get; }

        /// <summary>
        /// Gets the failed primitive's arguments with their source names, e.g. <c>d={a, b} v="b"</c>; <c>null</c> for
        /// merge failures.
        /// </summary>
        public string? Bindings { get; }

    }

}
