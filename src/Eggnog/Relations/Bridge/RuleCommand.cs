using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A right-hand-side command of the rule language of egglog-bridge's macros (egglog-bridge/src/macros.rs:50-72):
    /// <c>(let name term)</c>, <c>(set (f args...) value)</c> or <c>(union left right)</c>. Immutable.
    /// </summary>
    internal sealed class RuleCommand
    {

        RuleCommand(RuleCommandKind kind, string? name, RuleTerm left, RuleTerm right)
        {
            Kind = kind;
            Name = name;
            Left = left;
            Right = right;
        }

        /// <summary>
        /// Gets which case this is.
        /// </summary>
        public RuleCommandKind Kind { get; }

        /// <summary>
        /// Gets the name a <see cref="RuleCommandKind.Let"/> binds.
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// Gets the term of a <see cref="RuleCommandKind.Let"/>, the call of a <see cref="RuleCommandKind.Set"/>, or the
        /// left term of a <see cref="RuleCommandKind.Union"/>.
        /// </summary>
        public RuleTerm Left { get; }

        /// <summary>
        /// Gets the value of a <see cref="RuleCommandKind.Set"/> or the right term of a <see cref="RuleCommandKind.Union"/>;
        /// the same as <see cref="Left"/> for a <see cref="RuleCommandKind.Let"/>.
        /// </summary>
        public RuleTerm Right { get; }

        /// <summary>
        /// Creates <c>(let name term)</c>: looks <paramref name="term"/> up (minting it, for a constructor) and names the
        /// result. As in egglog, a name already bound keeps its first binding.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="term">The term; a call.</param>
        /// <returns>The command.</returns>
        /// <exception cref="ArgumentException"><paramref name="term"/> is not a call.</exception>
        public static RuleCommand Let(string name, RuleTerm term)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(term);
            if (term.Kind != RuleTermKind.Call)
                throw new ArgumentException("let binds the result of a call.", nameof(term));

            return new(RuleCommandKind.Let, name, term, term);
        }

        /// <summary>
        /// Creates <c>(set (f args...) value)</c>: writes the row of <paramref name="call"/>'s key with output
        /// <paramref name="value"/>.
        /// </summary>
        /// <param name="call">The function and key.</param>
        /// <param name="value">The output.</param>
        /// <returns>The command.</returns>
        /// <exception cref="ArgumentException"><paramref name="call"/> is not a call.</exception>
        public static RuleCommand Set(RuleTerm call, RuleTerm value)
        {
            ArgumentNullException.ThrowIfNull(call);
            ArgumentNullException.ThrowIfNull(value);
            if (call.Kind != RuleTermKind.Call)
                throw new ArgumentException("set writes a call's row.", nameof(call));

            return new(RuleCommandKind.Set, null, call, value);
        }

        /// <summary>
        /// Creates <c>(union left right)</c>.
        /// </summary>
        /// <param name="left">One term.</param>
        /// <param name="right">The other term.</param>
        /// <returns>The command.</returns>
        public static RuleCommand Union(RuleTerm left, RuleTerm right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            return new(RuleCommandKind.Union, null, left, right);
        }

        /// <inheritdoc />
        public override string ToString() => Kind switch
        {
            RuleCommandKind.Let => $"(let {Name} {Left})",
            RuleCommandKind.Set => $"(set {Left} {Right})",
            _ => $"(union {Left} {Right})",
        };

    }

}
