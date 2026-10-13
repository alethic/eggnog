using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class ExecutionState
    {

        /// <summary>
        /// Runs <paramref name="instrs"/> over the matches in <paramref name="bindings"/> and returns the number of
        /// matches that reach the end: egglog's <c>run_instrs</c> (core-relations/src/action/mod.rs:598-615), the
        /// vectorized right-hand-side interpreter.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each instruction runs over every live lane before the next one starts, under a <see cref="Mask"/> of the live
        /// lanes: lookups that miss, external functions that produce nothing and failed assertions drop lanes, and the
        /// program stops as soon as no lane is live. One <c>switch</c> per instruction per batch picks the kernel;
        /// inside a kernel the lane loop is specialized for the operation (see <see cref="VectorizedLookup"/>,
        /// <see cref="ArgGather"/>).
        /// </para>
        /// <para>
        /// If the join bound no variable (<see cref="Bindings.IdCount"/> is zero), the batch counts as one match, so a
        /// rule whose join binds nothing runs once per batch however many times it matched, as in egglog.
        /// </para>
        /// <para>
        /// Reads see the committed tables. Writes are staged into this state's buffers and become visible after
        /// <see cref="Flush"/> and the next merge. The only feedback within a run is the predicted-value cache
        /// (<see cref="LookupOrInsertDefaultInstr"/>) and whatever external functions do. The stop flag is not checked
        /// here; the join checks it between batches, as egglog's does.
        /// </para>
        /// </remarks>
        /// <param name="instrs">The program.</param>
        /// <param name="bindings">The batch; instructions add the variables they define.</param>
        /// <returns>The number of lanes live after the last instruction.</returns>
        public int RunInstrs(ReadOnlySpan<Instr> instrs, Bindings bindings)
        {
            ArgumentNullException.ThrowIfNull(bindings);

            // A join that binds no variable maps none on its first push: run the rule once.
            if (bindings.IdCount == 0)
                bindings.Matches = 1;

            var mask = new Mask(bindings.Matches);
            foreach (var instr in instrs)
            {
                if (mask.IsEmpty)
                    return 0;

                RunInstr(ref mask, instr, bindings);
            }

            return mask.Count;
        }

        /// <summary>
        /// Runs <paramref name="instrs"/> over the matches in <paramref name="bindings"/>: the array form of
        /// <see cref="RunInstrs(ReadOnlySpan{Instr}, Bindings)"/>.
        /// </summary>
        /// <param name="instrs">The program.</param>
        /// <param name="bindings">The batch.</param>
        /// <returns>The number of lanes live after the last instruction.</returns>
        public int RunInstrs(Instr[] instrs, Bindings bindings)
        {
            ArgumentNullException.ThrowIfNull(instrs);
            return RunInstrs(new ReadOnlySpan<Instr>(instrs), bindings);
        }

        /// <summary>
        /// Calls <paramref name="function"/> in every live lane on <paramref name="args"/> and binds the results to a
        /// new column for <paramref name="output"/>; lanes where it produces nothing get <see cref="Value.StaleRep"/>
        /// and are dropped. egglog's <c>invoke_batch</c> (core-relations/src/free_join/mod.rs:215-233).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="mask">The live lanes.</param>
        /// <param name="bindings">The batch.</param>
        /// <param name="args">The arguments.</param>
        /// <param name="output">The variable defined.</param>
        public void InvokeBatch(ExternalFunction function, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, Variable output)
        {
            ArgumentNullException.ThrowIfNull(function);
            CheckMask(mask, bindings);

            // The arguments are resolved before the output segment is added, as egglog reads them before it inserts the
            // output (see VectorizedLookup.Run).
            var cells = ActionScratch.RentCells(args.Length);
            var slots = ActionScratch.RentSlots(2 * args.Length);
            var call = new ExternalCallProducer(function, this, new ArgGather(args, bindings, cells, slots));
            var offset = bindings.AllocateColumn(output);
            mask.Fill(ref call, bindings.Data, offset, Value.StaleRep);
            ActionScratch.Return(cells);
            ActionScratch.Return(slots);
        }

        /// <summary>
        /// Calls <paramref name="function"/> in every live lane on <paramref name="args"/> and overwrites
        /// <paramref name="output"/>'s cell in that lane with the result; lanes where it produces nothing are dropped and
        /// keep their cell. egglog's <c>invoke_batch_assign</c> (core-relations/src/free_join/mod.rs:235-253).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="mask">The live lanes.</param>
        /// <param name="bindings">The batch.</param>
        /// <param name="args">The arguments.</param>
        /// <param name="output">The variable overwritten; already bound.</param>
        /// <exception cref="InvalidOperationException"><paramref name="output"/> is not bound.</exception>
        public void InvokeBatchAssign(ExternalFunction function, ref Mask mask, Bindings bindings, ReadOnlySpan<QueryEntry> args, Variable output)
        {
            ArgumentNullException.ThrowIfNull(function);
            CheckMask(mask, bindings);

            var offset = bindings.RequireOffset(output);
            var cells = ActionScratch.RentCells(args.Length);
            var slots = ActionScratch.RentSlots(2 * args.Length);
            var call = new ExternalCallProducer(function, this, new ArgGather(args, bindings, cells, slots));
            mask.AssignAndRetain(ref call, bindings.Data, offset);
            ActionScratch.Return(cells);
            ActionScratch.Return(slots);
        }

        /// <summary>
        /// Runs one instruction: egglog's <c>run_instr</c> (core-relations/src/action/mod.rs:616-882). Switches on the
        /// kind and reinterprets the instruction as its sealed class, never a virtual call.
        /// </summary>
        void RunInstr(ref Mask mask, Instr instr, Bindings bindings)
        {
            switch (instr.Kind)
            {
                case InstrKind.LookupOrInsertDefault:
                    RunLookupOrInsertDefault(ref mask, Unsafe.As<LookupOrInsertDefaultInstr>(instr), bindings);
                    break;
                case InstrKind.LookupWithDefault:
                {
                    var i = Unsafe.As<LookupWithDefaultInstr>(instr);
                    if (layers is { } l)
                        LayeredLookupWithDefault(l, i.Table, ref mask, bindings, i.Args, i.Column, i.Default, i.Destination);
                    else
                        VectorizedLookup.LookupWithDefault(ReadTable(i.Table), ref mask, bindings, i.Args, i.Column, i.Default, i.Destination);

                    break;
                }
                case InstrKind.Lookup:
                {
                    var i = Unsafe.As<LookupInstr>(instr);
                    if (layers is { } l)
                        LayeredLookupRow(l, i.Table, ref mask, bindings, i.Args, i.Column, i.Destination);
                    else
                        VectorizedLookup.LookupRow(ReadTable(i.Table), ref mask, bindings, i.Args, i.Column, i.Destination);

                    break;
                }
                case InstrKind.LookupWithFallback:
                    RunLookupWithFallback(ref mask, Unsafe.As<LookupWithFallbackInstr>(instr), bindings);
                    break;
                case InstrKind.Insert:
                {
                    var i = Unsafe.As<InsertInstr>(instr);
                    RunStage(ref mask, i.Table, i.Values, bindings, false);
                    break;
                }
                case InstrKind.InsertIfEq:
                    RunInsertIfEq(ref mask, Unsafe.As<InsertIfEqInstr>(instr), bindings);
                    break;
                case InstrKind.Remove:
                {
                    var i = Unsafe.As<RemoveInstr>(instr);
                    RunStage(ref mask, i.Table, i.Args, bindings, true);
                    break;
                }
                case InstrKind.External:
                {
                    var i = Unsafe.As<ExternalInstr>(instr);
                    InvokeBatch(GetExternalFunction(i.Function), ref mask, bindings, i.Args, i.Destination);
                    break;
                }
                case InstrKind.ExternalWithFallback:
                    RunExternalWithFallback(ref mask, Unsafe.As<ExternalWithFallbackInstr>(instr), bindings);
                    break;
                case InstrKind.AssertEq:
                {
                    var i = Unsafe.As<AssertEqInstr>(instr);
                    RunAssert(ref mask, i.Left, i.Right, bindings, true);
                    break;
                }
                case InstrKind.AssertNe:
                {
                    var i = Unsafe.As<AssertNeInstr>(instr);
                    RunAssert(ref mask, i.Left, i.Right, bindings, false);
                    break;
                }
                case InstrKind.AssertAnyNe:
                    RunAssertAnyNe(ref mask, Unsafe.As<AssertAnyNeInstr>(instr), bindings);
                    break;
                case InstrKind.ReadCounter:
                    RunReadCounter(Unsafe.As<ReadCounterInstr>(instr), bindings);
                    break;
                default:
                    ThrowUnknownInstr(instr.Kind);
                    break;
            }
        }

        /// <summary>
        /// <c>LookupOrInsertDefault</c> (core-relations/src/action/mod.rs:643-714): look the key up under a copy of the
        /// mask; for the live lanes that missed, take the row this state predicted for the key, or build
        /// <c>key ++ defaults</c>, stage it and remember it; then write the destination column of the row. Drops no lane.
        /// </summary>
        /// <remarks>
        /// In a child context the lookup reads the context's view, and the table's buffer is created only at the first
        /// miss, because creating it creates the context's layer of the table: a lookup that hits creates no layer. The
        /// row width is checked against the store's schema.
        /// </remarks>
        void RunLookupOrInsertDefault(ref Mask mask, LookupOrInsertDefaultInstr instr, Bindings bindings)
        {
            // egglog creates the table's buffer before the lookup, whether or not anything is staged; a child creates it
            // at the first miss, since creating it creates the context's layer.
            var layered = layers;
            var buffer = layered is null ? GetBuffer(instr.Table) : null;
            var args = instr.Args;
            var misses = mask;
            if (layered is not null)
                LayeredLookupRow(layered, instr.Table, ref misses, bindings, args, instr.Column, instr.Destination);
            else
                VectorizedLookup.LookupRow(ReadTable(instr.Table), ref misses, bindings, args, instr.Column, instr.Destination);

            misses.SymmetricDifference(mask);
            if (misses.IsEmpty)
                return;

            var defaults = instr.Defaults;
            var keyLength = args.Length;
            var rowLength = keyLength + defaults.Length;
            var arity = layered is null ? buffer!.Arity : layered.Schema.GetSpec(instr.Table).Arity;
            if (rowLength != arity)
                throw new InvalidOperationException($"{instr} builds rows of {rowLength} cells for a table of {arity} columns.");

            buffer ??= GetBuffer(instr.Table);

            var column = instr.Column.Rep;
            var data = bindings.Data;
            var destination = bindings.RequireOffset(instr.Destination);

            // Resolve the variable defaults once, so a missing variable fails before anything is staged.
            foreach (var d in defaults)
                if (d.Kind == WriteValKind.Entry && d.Entry.IsVariable)
                    bindings.RequireOffset(d.Entry.Variable);

            var p = Predicted;
            var cells = ActionScratch.RentCells(keyLength);
            var slots = ActionScratch.RentSlots(2 * keyLength);
            var key = new ArgGather(args, bindings, cells, slots);
            var staged = false;
            foreach (var lane in misses)
            {
                key.Load(lane);
                var offset = p.FindOrBegin(instr.Table, cells, 0, keyLength, rowLength, out var found);
                var row = p.Cells;
                if (found == false)
                {
                    for (var i = 0; i < defaults.Length; i++)
                        row[offset + keyLength + i] = DefaultValue(defaults[i], bindings, data, lane, row, offset);

                    buffer.StageInsert(new ReadOnlySpan<uint>(row, offset, rowLength));
                    p.Commit();
                    if (staged == false)
                    {
                        staged = true;
                        catalog.Notifications.Notify(instr.Table);

                        // egglog stages the default row without setting `changed`
                        // (core-relations/src/action/mod.rs:707), so a merge function that inserts through this path
                        // under-reports its change. Here, as on every other staging path, staging a row sets the flag.
                        Changed = true;
                    }
                }

                data[destination + lane] = row[offset + column];
            }

            ActionScratch.Return(cells);
            ActionScratch.Return(slots);
        }

        /// <summary>
        /// Evaluates one default of a row being built: egglog's <c>WriteVal</c> match in <c>LookupOrInsertDefault</c>
        /// (core-relations/src/action/mod.rs:693-704).
        /// </summary>
        uint DefaultValue(WriteVal value, Bindings bindings, uint[] data, int lane, uint[] row, int rowOffset)
        {
            switch (value.Kind)
            {
                case WriteValKind.Entry:
                    var entry = value.Entry;
                    return entry.IsVariable ? data[bindings.RequireOffset(entry.Variable) + lane] : entry.Constant.Rep;
                case WriteValKind.IncrementCounter:
                    return ValueFromCounter(IncrementCounter(value.Counter));
                default:
                    // The instruction checked that the copied cell comes before the one being written.
                    return row[rowOffset + value.Column];
            }
        }

        /// <summary>
        /// <c>LookupWithFallback</c> (core-relations/src/action/mod.rs:737-773): look the key up under a copy of the
        /// mask, call the fallback on the live lanes that missed, and keep the lanes where either succeeded.
        /// </summary>
        void RunLookupWithFallback(ref Mask mask, LookupWithFallbackInstr instr, Bindings bindings)
        {
            var found = mask;
            if (layers is { } l)
                LayeredLookupRow(l, instr.Table, ref found, bindings, instr.Key, instr.Column, instr.Destination);
            else
                VectorizedLookup.LookupRow(ReadTable(instr.Table), ref found, bindings, instr.Key, instr.Column, instr.Destination);

            var call = found;
            call.SymmetricDifference(mask);
            if (call.IsEmpty)
                return;

            InvokeBatchAssign(GetExternalFunction(instr.Function), ref call, bindings, instr.FunctionArgs, instr.Destination);
            found.Union(call);
            mask = found;
        }

        /// <summary>
        /// <c>ExternalWithFallback</c> (core-relations/src/action/mod.rs:829-862): call the first function under a copy
        /// of the mask, the fallback on the live lanes where it failed, and keep the lanes where either succeeded.
        /// </summary>
        void RunExternalWithFallback(ref Mask mask, ExternalWithFallbackInstr instr, Bindings bindings)
        {
            var succeeded = mask;
            InvokeBatch(GetExternalFunction(instr.Function), ref succeeded, bindings, instr.Args, instr.Destination);
            var call = succeeded;
            call.SymmetricDifference(mask);
            if (call.IsEmpty)
                return;

            InvokeBatchAssign(GetExternalFunction(instr.Fallback), ref call, bindings, instr.FallbackArgs, instr.Destination);
            succeeded.Union(call);
            mask = succeeded;
        }

        /// <summary>
        /// <c>Insert</c> and <c>Remove</c> (core-relations/src/action/mod.rs:774-780, 812-818): stage a row or a removal
        /// per live lane. The buffer is looked up once, and the table is notified and <see cref="Changed"/> set once,
        /// where egglog does both per lane; the effect is the same.
        /// </summary>
        void RunStage(ref Mask mask, TableId table, QueryEntry[] operands, Bindings bindings, bool remove)
        {
            Debug.Assert(mask.IsEmpty == false, "the interpreter stops before an instruction with no live lane");

            // A rule's delete is a retraction, so it passes the state's retraction guard, once per batch: the guard
            // refuses removals from a shared table while the context has live descendants, or in a child whose parent
            // is not frozen.
            if (remove)
                guard?.CheckRemove(table);

            var buffer = GetBuffer(table);
            var cells = ActionScratch.RentCells(operands.Length);
            var slots = ActionScratch.RentSlots(2 * operands.Length);
            var gather = new ArgGather(operands, bindings, cells, slots);
            if (remove)
            {
                var visitor = new StageRemoveVisitor(buffer, gather);
                mask.ForEach(ref visitor);
            }
            else
            {
                var visitor = new StageInsertVisitor(buffer, gather);
                mask.ForEach(ref visitor);
            }

            ActionScratch.Return(cells);
            ActionScratch.Return(slots);
            catalog.Notifications.Notify(table);
            Changed = true;
        }

        /// <summary>
        /// <c>InsertIfEq</c> (core-relations/src/action/mod.rs:781-811): stage a row per live lane where the two
        /// operands are equal. Two constants are compared once. Drops no lane.
        /// </summary>
        void RunInsertIfEq(ref Mask mask, InsertIfEqInstr instr, Bindings bindings)
        {
            if (instr.Left.IsVariable == false && instr.Right.IsVariable == false)
            {
                if (instr.Left.Constant == instr.Right.Constant)
                    RunStage(ref mask, instr.Table, instr.Values, bindings, false);

                return;
            }

            var buffer = GetBuffer(instr.Table);
            var values = instr.Values;
            var cells = ActionScratch.RentCells(values.Length);
            var slots = ActionScratch.RentSlots(2 * values.Length);
            var visitor = new ConditionalInsertVisitor(buffer, new ArgGather(values, bindings, cells, slots), ArgSource.Of(instr.Left, bindings), ArgSource.Of(instr.Right, bindings));
            mask.ForEach(ref visitor);
            ActionScratch.Return(cells);
            ActionScratch.Return(slots);
            if (visitor.Staged > 0)
            {
                catalog.Notifications.Notify(instr.Table);
                Changed = true;
            }
        }

        /// <summary>
        /// <c>AssertEq</c> and <c>AssertNe</c> (core-relations/src/action/mod.rs:617-640, 873-874): keep the lanes where
        /// the operands compare as required; two constants are compared once and either keep or drop every lane.
        /// </summary>
        static void RunAssert(ref Mask mask, QueryEntry left, QueryEntry right, Bindings bindings, bool keepEqual)
        {
            if (left.IsVariable == false && right.IsVariable == false)
            {
                if ((left.Constant == right.Constant) != keepEqual)
                    mask.Clear();

                return;
            }

            var filter = new EqualityFilter(ArgSource.Of(left, bindings), ArgSource.Of(right, bindings), keepEqual);
            mask.Retain(ref filter);
        }

        /// <summary>
        /// <c>AssertAnyNe</c> (core-relations/src/action/mod.rs:863-872): keep the lanes where the two operand lists
        /// differ at some position.
        /// </summary>
        static void RunAssertAnyNe(ref Mask mask, AssertAnyNeInstr instr, Bindings bindings)
        {
            var ops = instr.Ops;
            var cells = ActionScratch.RentCells(ops.Length);
            var slots = ActionScratch.RentSlots(2 * ops.Length);
            var filter = new AnyNotEqualFilter(new ArgGather(ops, bindings, cells, slots), instr.Divider, instr.PairCount);
            mask.Retain(ref filter);
            ActionScratch.Return(cells);
            ActionScratch.Return(slots);
        }

        /// <summary>
        /// <c>ReadCounter</c> (core-relations/src/action/mod.rs:875-880): bind the counter's current value in every lane
        /// of the batch, live or not.
        /// </summary>
        void RunReadCounter(ReadCounterInstr instr, Bindings bindings)
        {
            var value = ValueFromCounter(ReadCounter(instr.Counter));
            var offset = bindings.AllocateColumn(instr.Destination);
            bindings.Data.AsSpan(offset, bindings.Matches).Fill(value);
        }

        /// <summary>
        /// Gets the external function with id <paramref name="function"/> from the database view.
        /// </summary>
        ExternalFunction GetExternalFunction(ExternalFunctionId function) => RequireView().GetExternalFunction(function);

        /// <summary>
        /// Throws unless every lane of <paramref name="mask"/> lies in a segment of <paramref name="bindings"/>.
        /// </summary>
        static void CheckMask(in Mask mask, Bindings bindings)
        {
            ArgumentNullException.ThrowIfNull(bindings);
            if (mask.Length > bindings.MaxBatchSize)
                throw new ArgumentException($"The mask has {mask.Length} lanes but the batch holds at most {bindings.MaxBatchSize}.", nameof(mask));
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowUnknownInstr(InstrKind kind)
        {
            throw new InvalidOperationException($"Unknown instruction kind {kind}.");
        }

    }

}
