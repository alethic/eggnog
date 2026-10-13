using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Gathers an instruction's operand list, lane by lane, into a scratch array: what egglog's
    /// <c>for_each_binding_with_mask!</c> and <c>MaskIterDynamicSource</c> do (core-relations/src/action/mask.rs:247-304,
    /// 419-566), without the per-lane allocation of egglog's dynamic path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor writes every constant operand into its cell once and records, for each variable operand, its
    /// position and the offset of its column in the bindings. <see cref="Load"/> then copies only the variable
    /// operands of one lane: <see cref="Values"/> holds the lane's operand tuple, ready for a table probe, a staged row
    /// or an external function call. Any operand shape and arity takes the same path.
    /// </para>
    /// <para>
    /// The scratch arrays belong to the caller (see <see cref="ActionScratch"/>). The gather holds the bindings' data
    /// array as it was when the gather was made, and the offsets of the segments its operands were mapped to then: make
    /// it after every segment its operands read exists. Segments added afterwards do not change what it reads: a new
    /// segment occupies cells no earlier segment uses, and if adding one replaces the data array the gather keeps
    /// reading the old array, which still holds the segments it was made against. The interpreter relies on this: it
    /// makes the gather before it adds the instruction's destination segment, so an instruction that redefines one of
    /// its own operands reads the operand's old values, as egglog does. A <see cref="Bindings.Clear"/> invalidates it.
    /// </para>
    /// </remarks>
    internal readonly struct ArgGather
    {

        readonly uint[] cells;
        readonly int[] slots;
        readonly uint[] data;
        readonly int count;
        readonly int variables;
        readonly int lanes;

        /// <summary>
        /// Prepares to gather <paramref name="entries"/> from <paramref name="bindings"/> into <paramref name="cells"/>.
        /// O(operands).
        /// </summary>
        /// <param name="entries">The operands.</param>
        /// <param name="bindings">The batch.</param>
        /// <param name="cells">Receives each lane's operand tuple; at least one cell per operand.</param>
        /// <param name="slots">Scratch for the variable operands; at least two cells per operand.</param>
        /// <exception cref="InvalidOperationException">An operand is a variable the batch does not bind.</exception>
        public ArgGather(ReadOnlySpan<QueryEntry> entries, Bindings bindings, uint[] cells, int[] slots)
        {
            if (cells.Length < entries.Length || slots.Length < 2 * entries.Length)
                ThrowScratch();

            var v = 0;
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.IsVariable)
                {
                    slots[2 * v] = i;
                    slots[2 * v + 1] = bindings.RequireOffset(entry.Variable);
                    v++;
                }
                else
                {
                    cells[i] = entry.Constant.Rep;
                }
            }

            this.cells = cells;
            this.slots = slots;
            data = bindings.Data;
            count = entries.Length;
            variables = v;
            lanes = bindings.MaxBatchSize;
        }

        /// <summary>
        /// Gets the array holding the current lane's operand tuple in its first <see cref="Count"/> cells.
        /// </summary>
        public uint[] Cells => cells;

        /// <summary>
        /// Gets the number of operands.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets the current lane's operand tuple.
        /// </summary>
        public ReadOnlySpan<uint> Values => new(cells, 0, count);

        /// <summary>
        /// Loads the operand tuple of <paramref name="lane"/> into <see cref="Cells"/>. O(variable operands).
        /// </summary>
        /// <param name="lane">A lane of the batch.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Load(int lane)
        {
            // The constructor checked the scratch lengths, and every recorded offset starts a whole segment of the data
            // array, so one check of the lane makes the reads and writes below safe.
            if ((uint)lane >= (uint)lanes)
                ThrowLane(lane);

            ref var slot = ref MemoryMarshal.GetArrayDataReference(slots);
            ref var cell = ref MemoryMarshal.GetArrayDataReference(cells);
            ref var source = ref MemoryMarshal.GetArrayDataReference(data);
            for (var k = 0; k < variables; k++)
            {
                var position = Unsafe.Add(ref slot, 2 * k);
                var offset = Unsafe.Add(ref slot, 2 * k + 1);
                Unsafe.Add(ref cell, position) = Unsafe.Add(ref source, offset + lane);
            }
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowScratch()
        {
            throw new ArgumentException("The scratch arrays are too small for the operands.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowLane(int lane)
        {
            throw new ArgumentOutOfRangeException(nameof(lane), lane, "The lane is outside the batch.");
        }

    }

}
