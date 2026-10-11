using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Where one operand of an instruction comes from in a batch: a variable's column of the <see cref="Bindings"/>, or a
    /// constant. Resolved once per instruction per batch; <see cref="Get"/> then reads the operand's value in a lane.
    /// The single-operand counterpart of the shape specialization in egglog's <c>for_each_binding_with_mask!</c>
    /// (core-relations/src/action/mask.rs:419-566).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The variable-or-constant test in <see cref="Get"/> has the same outcome in every lane of a batch, so the branch
    /// predicts perfectly.
    /// </para>
    /// <para>
    /// The source holds the bindings' data array as it was when the source was resolved, and the offset its variable
    /// was mapped to then: resolve it after the segment it reads exists. Segments added afterwards do not change what
    /// it reads: a new segment occupies cells no earlier segment uses, and if adding one replaces the data array the
    /// source keeps reading the old array, which still holds its segment. The interpreter relies on this: it resolves
    /// operands before it adds the instruction's destination segment, so an instruction that redefines one of its own
    /// operands reads the operand's old values, as egglog does. A <see cref="Bindings.Clear"/> invalidates it.
    /// </para>
    /// </remarks>
    internal readonly struct ArgSource
    {

        readonly uint[] data;
        readonly int offset;
        readonly uint constant;
        readonly bool isConstant;

        ArgSource(uint[] data, int offset, uint constant, bool isConstant)
        {
            this.data = data;
            this.offset = offset;
            this.constant = constant;
            this.isConstant = isConstant;
        }

        /// <summary>
        /// Gets whether the operand is a constant.
        /// </summary>
        public bool IsConstant => isConstant;

        /// <summary>
        /// Gets the constant of a constant operand.
        /// </summary>
        public uint Constant => constant;

        /// <summary>
        /// Resolves <paramref name="entry"/> against <paramref name="bindings"/>.
        /// </summary>
        /// <param name="entry">The operand.</param>
        /// <param name="bindings">The batch.</param>
        /// <returns>The source.</returns>
        /// <exception cref="InvalidOperationException">The operand is a variable the batch does not bind.</exception>
        public static ArgSource Of(QueryEntry entry, Bindings bindings)
        {
            if (entry.IsVariable == false)
                return new ArgSource(Array.Empty<uint>(), 0, entry.Constant.Rep, true);

            return new ArgSource(bindings.Data, bindings.RequireOffset(entry.Variable), 0, false);
        }

        /// <summary>
        /// Gets the operand's value in <paramref name="lane"/>.
        /// </summary>
        /// <param name="lane">A lane of the batch.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Get(int lane) => isConstant ? constant : data[offset + lane];

    }

}
