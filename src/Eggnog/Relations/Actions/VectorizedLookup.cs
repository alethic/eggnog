using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Point lookups over a whole batch of matches: egglog's <c>TableWrapper::lookup_row_vectorized</c> and
    /// <c>lookup_with_default_vectorized</c> (core-relations/src/table_spec.rs:490-544), the table side of the lookup
    /// instructions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// egglog downcasts the table once per batch and runs a loop monomorphized for its type. Here the lookup switches on
    /// <see cref="Table.Kind"/> once, picks a probe specialized for the table class and the key width
    /// (<see cref="SortedKey1Lookup"/> to <see cref="SortedKey3Lookup"/> with the key in registers,
    /// <see cref="SortedKeyNLookup"/> otherwise, <see cref="DisplacedLookup"/> for the union-find,
    /// <see cref="OverlayDisplacedLookup"/> for a child context's union-find), and runs
    /// <see cref="Mask.Fill{TProducer}"/> specialized for that probe: no virtual call per lane.
    /// </para>
    /// <para>
    /// In a child context the lookups run over the context's <see cref="TableView"/> instead, with the
    /// <c>LayeredKey*Lookup</c> probes: the first layer whose key index holds the key decides.
    /// </para>
    /// <para>
    /// The result goes into a new segment of the bindings for the output variable, with
    /// <see cref="Value.StaleRep"/> in dead lanes (and, for a plain lookup, in lanes whose key is missing, which are
    /// dropped from the mask). Lookups read the committed table: rows staged in the current run are not visible.
    /// </para>
    /// </remarks>
    internal static class VectorizedLookup
    {

        /// <summary>
        /// Binds <paramref name="output"/> to column <paramref name="column"/> of the row with key
        /// <paramref name="args"/> in each live lane, and drops the lanes whose key is missing. egglog's
        /// <c>lookup_row_vectorized</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="mask">The live lanes; lanes whose key is missing are dropped.</param>
        /// <param name="bindings">The batch.</param>
        /// <param name="args">The key; one operand per key column.</param>
        /// <param name="column">The column to read.</param>
        /// <param name="output">The variable defined.</param>
        public static void LookupRow(Table table, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, ColumnId column, Variable output)
        {
            Run(table, ref mask, bindings, args, column, output, false, default);
        }

        /// <summary>
        /// Binds <paramref name="output"/> to column <paramref name="column"/> of the row with key
        /// <paramref name="args"/> in each live lane, or to <paramref name="default"/> where the key is missing. egglog's
        /// <c>lookup_with_default_vectorized</c>. Drops no lane.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="mask">The live lanes.</param>
        /// <param name="bindings">The batch.</param>
        /// <param name="args">The key; one operand per key column.</param>
        /// <param name="column">The column to read.</param>
        /// <param name="default">The value bound where the key is missing.</param>
        /// <param name="output">The variable defined.</param>
        public static void LookupWithDefault(Table table, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, ColumnId column, QueryEntry @default, Variable output)
        {
            Run(table, ref mask, bindings, args, column, output, true, @default);
        }

        /// <summary>
        /// Binds <paramref name="output"/> to column <paramref name="column"/> of the row a child context sees with key
        /// <paramref name="args"/> in each live lane, and drops the lanes whose key is missing: the layered form of
        /// <see cref="LookupRow(Table, ref Mask, Bindings, ReadOnlySpan{QueryEntry}, ColumnId, Variable)"/> over the
        /// context's view.
        /// </summary>
        /// <param name="view">The context's view of the table.</param>
        /// <param name="mask">The live lanes; lanes whose key is missing are dropped.</param>
        /// <param name="bindings">The batch.</param>
        /// <param name="args">The key; one operand per key column.</param>
        /// <param name="column">The column to read.</param>
        /// <param name="output">The variable defined.</param>
        public static void LookupRow(TableView view, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, ColumnId column, Variable output)
        {
            RunLayered(view, ref mask, bindings, args, column, output, false, default);
        }

        /// <summary>
        /// Binds <paramref name="output"/> to column <paramref name="column"/> of the row a child context sees with key
        /// <paramref name="args"/> in each live lane, or to <paramref name="default"/> where the key is missing: the
        /// layered form of <see cref="LookupWithDefault(Table, ref Mask, Bindings, ReadOnlySpan{QueryEntry}, ColumnId, QueryEntry, Variable)"/>.
        /// Drops no lane.
        /// </summary>
        /// <param name="view">The context's view of the table.</param>
        /// <param name="mask">The live lanes.</param>
        /// <param name="bindings">The batch.</param>
        /// <param name="args">The key; one operand per key column.</param>
        /// <param name="column">The column to read.</param>
        /// <param name="default">The value bound where the key is missing.</param>
        /// <param name="output">The variable defined.</param>
        public static void LookupWithDefault(TableView view, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, ColumnId column, QueryEntry @default, Variable output)
        {
            RunLayered(view, ref mask, bindings, args, column, output, true, @default);
        }

        /// <summary>
        /// Checks the arguments of a layered lookup, and picks the probe by key width (<see cref="LayeredKey1Lookup"/> to
        /// <see cref="LayeredKey3Lookup"/> with the key in registers, <see cref="LayeredKeyNLookup"/> otherwise).
        /// </summary>
        static void RunLayered(TableView view, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, ColumnId column, Variable output, bool hasDefault, QueryEntry @default)
        {
            ArgumentNullException.ThrowIfNull(view);
            ArgumentNullException.ThrowIfNull(bindings);
            if (mask.Length > bindings.MaxBatchSize)
                ThrowMaskTooLong(mask.Length, bindings.MaxBatchSize);
            if ((uint)column.Rep >= (uint)view.Spec.Arity)
                throw new ArgumentOutOfRangeException(nameof(column), column, $"The table has {view.Spec.Arity} columns.");
            if (args.Length != view.Spec.KeyCount)
                throw new ArgumentException($"The table has {view.Spec.KeyCount} key columns but the key has {args.Length} operands.", nameof(args));

            // As in Run: the operands are resolved before the output segment is added.
            var fallback = hasDefault ? ArgSource.Of(@default, bindings) : default;
            switch (args.Length)
            {
                case 1:
                {
                    var lookup = new LayeredKey1Lookup(view, ArgSource.Of(args[0], bindings), column.Rep);
                    Apply(ref mask, ref lookup, bindings, output, hasDefault, fallback);
                    return;
                }
                case 2:
                {
                    var lookup = new LayeredKey2Lookup(view, ArgSource.Of(args[0], bindings), ArgSource.Of(args[1], bindings), column.Rep);
                    Apply(ref mask, ref lookup, bindings, output, hasDefault, fallback);
                    return;
                }
                case 3:
                {
                    var lookup = new LayeredKey3Lookup(view, ArgSource.Of(args[0], bindings), ArgSource.Of(args[1], bindings), ArgSource.Of(args[2], bindings), column.Rep);
                    Apply(ref mask, ref lookup, bindings, output, hasDefault, fallback);
                    return;
                }
                default:
                {
                    var cells = ActionScratch.RentCells(args.Length);
                    var slots = ActionScratch.RentSlots(2 * args.Length);
                    var lookup = new LayeredKeyNLookup(view, new ArgGather(args, bindings, cells, slots), column.Rep);
                    Apply(ref mask, ref lookup, bindings, output, hasDefault, fallback);
                    ActionScratch.Return(cells);
                    ActionScratch.Return(slots);
                    return;
                }
            }
        }

        /// <summary>
        /// Checks the arguments, allocates the output column, and dispatches on the table class.
        /// </summary>
        static void Run(Table table, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, ColumnId column, Variable output, bool hasDefault, QueryEntry @default)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(bindings);
            if (mask.Length > bindings.MaxBatchSize)
                ThrowMaskTooLong(mask.Length, bindings.MaxBatchSize);
            if ((uint)column.Rep >= (uint)table.Arity)
                throw new ArgumentOutOfRangeException(nameof(column), column, $"The table has {table.Arity} columns.");
            if (args.Length != table.Spec.KeyCount)
                throw new ArgumentException($"The table has {table.Spec.KeyCount} key columns but the key has {args.Length} operands.", nameof(args));

            // The operands are resolved before the output segment is added, as egglog reads the arguments before it
            // inserts the output: a destination that redefines an operand still reads the operand's old values. If
            // adding the segment replaces the bindings' data array, the sources keep reading the old array, which still
            // holds every segment they were resolved against.
            var fallback = hasDefault ? ArgSource.Of(@default, bindings) : default;
            switch (table.Kind)
            {
                case TableKind.SortedWrites:
                    RunSorted(Unsafe.As<SortedWritesTable>(table), ref mask, bindings, args, column.Rep, output, hasDefault, fallback);
                    return;
                case TableKind.Displaced:
                {
                    var lookup = new DisplacedLookup(Unsafe.As<DisplacedTable>(table), ArgSource.Of(args[0], bindings), column.Rep);
                    Apply(ref mask, ref lookup, bindings, output, hasDefault, fallback);
                    return;
                }
                case TableKind.OverlayDisplaced:
                {
                    var lookup = new OverlayDisplacedLookup(Unsafe.As<OverlayDisplacedTable>(table), ArgSource.Of(args[0], bindings), column.Rep);
                    Apply(ref mask, ref lookup, bindings, output, hasDefault, fallback);
                    return;
                }
                default:
                    throw new InvalidOperationException($"Unknown table kind {table.Kind}.");
            }
        }

        /// <summary>
        /// Picks the probe for a sorted-writes table by key width and runs it.
        /// </summary>
        static void RunSorted(SortedWritesTable table, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, int column, Variable output, bool hasDefault, ArgSource fallback)
        {
            switch (args.Length)
            {
                case 1:
                {
                    var lookup = new SortedKey1Lookup(table, ArgSource.Of(args[0], bindings), column);
                    Apply(ref mask, ref lookup, bindings, output, hasDefault, fallback);
                    return;
                }
                case 2:
                {
                    var lookup = new SortedKey2Lookup(table, ArgSource.Of(args[0], bindings), ArgSource.Of(args[1], bindings), column);
                    Apply(ref mask, ref lookup, bindings, output, hasDefault, fallback);
                    return;
                }
                case 3:
                {
                    var lookup = new SortedKey3Lookup(table, ArgSource.Of(args[0], bindings), ArgSource.Of(args[1], bindings), ArgSource.Of(args[2], bindings), column);
                    Apply(ref mask, ref lookup, bindings, output, hasDefault, fallback);
                    return;
                }
                default:
                {
                    var cells = ActionScratch.RentCells(args.Length);
                    var slots = ActionScratch.RentSlots(2 * args.Length);
                    var lookup = new SortedKeyNLookup(table, new ArgGather(args, bindings, cells, slots), column);
                    Apply(ref mask, ref lookup, bindings, output, hasDefault, fallback);
                    ActionScratch.Return(cells);
                    ActionScratch.Return(slots);
                    return;
                }
            }
        }

        /// <summary>
        /// Adds the output segment and runs <paramref name="lookup"/> over the live lanes into it, wrapped to supply the
        /// default when there is one.
        /// </summary>
        static void Apply<TLookup>(ref Mask mask, ref TLookup lookup, Bindings bindings, Variable output, bool hasDefault, ArgSource fallback)
            where TLookup : struct, ILaneProducer
        {
            var offset = bindings.AllocateColumn(output);
            var data = bindings.Data;
            if (hasDefault)
            {
                var defaulting = new DefaultingLookup<TLookup>(lookup, fallback);
                mask.Fill(ref defaulting, data, offset, Value.StaleRep);
            }
            else
            {
                mask.Fill(ref lookup, data, offset, Value.StaleRep);
            }
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowMaskTooLong(int length, int maxBatchSize)
        {
            throw new ArgumentException($"The mask has {length} lanes but the batch holds at most {maxBatchSize}.", "mask");
        }

    }

}
