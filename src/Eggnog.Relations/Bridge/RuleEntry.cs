using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An operand of a rule built with an <see cref="EGraphRuleBuilder"/>: a variable of the rule, or a constant with the
    /// column type it was written for. egglog-bridge's <c>QueryEntry</c> (egglog-bridge/src/rule.rs:57-66), named apart
    /// from the core query's <see cref="QueryEntry"/>, into which it is translated when the rule is planned.
    /// </summary>
    /// <remarks>
    /// A constant's type is carried for diagnostics, as in egglog (a base value constant in egglog has one): type checks
    /// apply only to variables, and constants are untyped for them. A 32-byte value on a 64-bit runtime (the variable,
    /// its id and name reference, takes 16; the constant, its column type and the flag the rest); equality is
    /// structural. Entries are recorded and lowered on the cold path only, so the size costs nothing per match.
    /// </remarks>
    public readonly record struct RuleEntry
    {

        readonly RuleVariable variable;
        readonly Value constant;
        readonly ColumnTy type;

        RuleEntry(bool isVariable, RuleVariable variable, Value constant, ColumnTy type)
        {
            IsVariable = isVariable;
            this.variable = variable;
            this.constant = constant;
            this.type = type;
        }

        /// <summary>
        /// Creates the entry for <paramref name="variable"/>: egglog's <c>QueryEntry::Var</c>.
        /// </summary>
        /// <param name="variable">The variable.</param>
        /// <returns>The entry.</returns>
        public static RuleEntry Var(RuleVariable variable) => new(true, variable, default, default);

        /// <summary>
        /// Creates the entry for the constant <paramref name="value"/>: egglog's <c>QueryEntry::Const</c>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="type">The column type the constant is written for.</param>
        /// <returns>The entry.</returns>
        public static RuleEntry Const(Value value, ColumnTy type) => new(false, default, value, type);

        /// <summary>
        /// Gets whether this is a variable; otherwise it is a constant.
        /// </summary>
        public bool IsVariable { get; }

        /// <summary>
        /// Gets the variable of a variable entry.
        /// </summary>
        /// <exception cref="InvalidOperationException">The entry is a constant.</exception>
        public RuleVariable Variable
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IsVariable ? variable : throw new InvalidOperationException("A constant entry has no variable.");
        }

        /// <summary>
        /// Gets the value of a constant entry.
        /// </summary>
        /// <exception cref="InvalidOperationException">The entry is a variable.</exception>
        public Value Constant
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IsVariable == false ? constant : throw new InvalidOperationException("A variable entry has no constant.");
        }

        /// <summary>
        /// Gets the column type of a constant entry.
        /// </summary>
        /// <exception cref="InvalidOperationException">The entry is a variable.</exception>
        public ColumnTy ConstantType => IsVariable == false ? type : throw new InvalidOperationException("A variable entry has no constant type.");

        /// <inheritdoc />
        public override string ToString() => IsVariable ? variable.ToString() : constant.Rep + ":" + type;

    }

}
