using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the live lanes where the operand lists <c>Ops[0..Divider)</c> and <c>Ops[Divider..)</c> differ at some
    /// position. egglog's <c>Instr::AssertAnyNe</c> (core-relations/src/action/mod.rs:974-980, 863-872); the bridge's
    /// rebuild rules use it to skip rows that are already canonical.
    /// </summary>
    /// <remarks>
    /// The lists are compared pairwise over the shorter one's length, as egglog's <c>zip</c> does; the rule builder
    /// always makes them equally long.
    /// </remarks>
    internal sealed class AssertAnyNeInstr : Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="ops">Both operand lists, one after the other.</param>
        /// <param name="divider">The length of the first list.</param>
        public AssertAnyNeInstr(ReadOnlySpan<QueryEntry> ops, int divider)
            : base(InstrKind.AssertAnyNe)
        {
            if ((uint)divider > (uint)ops.Length)
                throw new ArgumentOutOfRangeException(nameof(divider), divider, "The divider lies outside the operands.");

            Ops = CopyOperands(ops, nameof(ops));
            Divider = divider;
        }

        /// <summary>
        /// Gets both operand lists, one after the other.
        /// </summary>
        public QueryEntry[] Ops { get; }

        /// <summary>
        /// Gets the length of the first list.
        /// </summary>
        public int Divider { get; }

        /// <summary>
        /// Gets the number of pairs compared: the shorter list's length.
        /// </summary>
        public int PairCount => Math.Min(Divider, Ops.Length - Divider);

        /// <summary>
        /// Creates the instruction comparing <paramref name="left"/> with <paramref name="right"/>: what egglog's
        /// <c>RuleBuilder::assert_any_ne</c> emits (core-relations/src/query.rs).
        /// </summary>
        /// <param name="left">The first list.</param>
        /// <param name="right">The second list, as long as the first.</param>
        /// <returns>The instruction.</returns>
        public static AssertAnyNeInstr Create(ReadOnlySpan<QueryEntry> left, ReadOnlySpan<QueryEntry> right)
        {
            if (left.Length != right.Length)
                throw new ArgumentException("The operand lists must be equally long.", nameof(right));

            var ops = new QueryEntry[left.Length + right.Length];
            left.CopyTo(ops);
            right.CopyTo(ops.AsSpan(left.Length));
            return new AssertAnyNeInstr(ops, left.Length);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder("AssertAnyNe ");
            AppendList(b, Ops[..Divider]).Append(' ');
            return AppendList(b, Ops[Divider..]).ToString();
        }

    }

}
