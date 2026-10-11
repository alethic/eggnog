using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A left-hand-side fact of the rule language of egglog-bridge's macros, <c>(-&gt; (f args...) output)</c>
    /// (egglog-bridge/src/macros.rs:131-142): the row of <c>f</c> for the key <c>args</c> has output <c>output</c>. Made
    /// by <see cref="RuleTerm.Is"/>. Immutable.
    /// </summary>
    public sealed class RuleFact
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="call">The call, whose function the fact queries.</param>
        /// <param name="output">The output: a variable or another call.</param>
        /// <exception cref="ArgumentException"><paramref name="call"/> is not a call, or <paramref name="output"/> is a
        /// constant.</exception>
        public RuleFact(RuleTerm call, RuleTerm output)
        {
            ArgumentNullException.ThrowIfNull(call);
            ArgumentNullException.ThrowIfNull(output);
            if (call.Kind != RuleTermKind.Call)
                throw new ArgumentException("A fact's left side must be a call.", nameof(call));
            if (output.Kind == RuleTermKind.Constant)
                throw new ArgumentException("A fact's output must be a variable or a call.", nameof(output));

            Call = call;
            Output = output;
        }

        /// <summary>
        /// Gets the call.
        /// </summary>
        public RuleTerm Call { get; }

        /// <summary>
        /// Gets the output.
        /// </summary>
        public RuleTerm Output { get; }

        /// <inheritdoc />
        public override string ToString() => $"(-> {Call} {Output})";

    }

}
