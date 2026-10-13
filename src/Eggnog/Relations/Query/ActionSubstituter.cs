using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Rewrites the Id constants of an action program into their canonical form in a context, and defines how an
    /// instruction's operands are numbered for <see cref="ActionIdConstant.Operand"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Operand order per <see cref="InstrKind"/>: the table operands first, then the rest.
    /// <list type="table">
    /// <item><term><see cref="InstrKind.LookupOrInsertDefault"/></term><description>keys <c>0..k</c>, then default
    /// <c>d</c> at <c>k + d</c> (only <see cref="WriteValKind.Entry"/> defaults hold an operand)</description></item>
    /// <item><term><see cref="InstrKind.LookupWithDefault"/></term><description>keys, then the default</description></item>
    /// <item><term><see cref="InstrKind.Lookup"/>, <see cref="InstrKind.Remove"/></term><description>keys</description></item>
    /// <item><term><see cref="InstrKind.LookupWithFallback"/></term><description>keys, then the function's
    /// arguments</description></item>
    /// <item><term><see cref="InstrKind.Insert"/></term><description>the row's values</description></item>
    /// <item><term><see cref="InstrKind.InsertIfEq"/></term><description>the row's values, then the left and the right
    /// comparand</description></item>
    /// <item><term><see cref="InstrKind.External"/></term><description>the arguments</description></item>
    /// <item><term><see cref="InstrKind.ExternalWithFallback"/></term><description>the arguments, then the fallback's
    /// arguments</description></item>
    /// <item><term><see cref="InstrKind.AssertEq"/>, <see cref="InstrKind.AssertNe"/></term><description>left,
    /// right</description></item>
    /// <item><term><see cref="InstrKind.AssertAnyNe"/></term><description>every operand of both lists, in
    /// order</description></item>
    /// <item><term><see cref="InstrKind.ReadCounter"/></term><description>none</description></item>
    /// </list>
    /// A table operand also has a column: key <c>i</c>, row value <c>i</c> and default <c>d</c> land in column <c>i</c>
    /// (or <c>k + d</c>) of the instruction's table, which is how the rule builder tells an id operand from another
    /// (<see cref="RuleBuilder"/>).
    /// </para>
    /// <para>
    /// A rewritten program shares every instruction that does not change; a changed one is a new instance with the
    /// canonical operands. Cold path: once per (rule, distinct K).
    /// </para>
    /// </remarks>
    internal static class ActionSubstituter
    {

        /// <summary>
        /// Creates the program <paramref name="actions"/> runs as under <paramref name="substitution"/>.
        /// </summary>
        /// <param name="actions">The written program.</param>
        /// <param name="substitution">The constants that change.</param>
        /// <returns>The rewritten program, or <paramref name="actions"/> itself when no action constant changes; it keeps
        /// the written <see cref="ActionInfo.IdConstants"/>.</returns>
        public static ActionInfo Substitute(ActionInfo actions, ConstantSubstitution substitution)
        {
            ArgumentNullException.ThrowIfNull(actions);
            ArgumentNullException.ThrowIfNull(substitution);
            if (substitution.ChangesActions == false)
                return actions;

            var instrs = actions.Instrs;
            Instr[]? copy = null;
            for (var i = 0; i < instrs.Length; i++)
            {
                if (substitution.ChangesInstr(i) == false)
                    continue;

                var operands = Operands(instrs[i]);
                var changed = false;
                for (var o = 0; o < operands.Length; o++)
                {
                    if (substitution.TryGetAction(i, o, out var canonical) == false)
                        continue;

                    var entry = operands[o].Entry;
                    if (operands[o].IsEntry == false || entry.IsVariable)
                        throw new InvalidOperationException($"Operand {o} of instruction {i} is not a constant.");

                    operands[o] = (entry.WithConstant(new Value(canonical)), true, operands[o].Column);
                    changed = true;
                }

                if (changed)
                {
                    copy ??= (Instr[])instrs.Clone();
                    copy[i] = WithOperands(instrs[i], operands);
                }
            }

            return copy is null ? actions : new ActionInfo(copy, actions.UsedVariables, actions.IdConstants);
        }

        /// <summary>
        /// Lists the operands of <paramref name="instr"/> in the order <see cref="ActionIdConstant.Operand"/> numbers
        /// them, each with whether it holds an entry (a default computed from a counter or another cell holds none) and
        /// the column of the instruction's table it lands in, or −1.
        /// </summary>
        /// <param name="instr">The instruction.</param>
        /// <returns>The operands.</returns>
        public static (QueryEntry Entry, bool IsEntry, int Column)[] Operands(Instr instr)
        {
            ArgumentNullException.ThrowIfNull(instr);
            switch (instr)
            {
                case LookupOrInsertDefaultInstr i:
                    {
                        var result = new (QueryEntry, bool, int)[i.Args.Length + i.Defaults.Length];
                        for (var k = 0; k < i.Args.Length; k++)
                            result[k] = (i.Args[k], true, k);

                        for (var d = 0; d < i.Defaults.Length; d++)
                        {
                            var value = i.Defaults[d];
                            result[i.Args.Length + d] = (value.Entry, value.Kind == WriteValKind.Entry, i.Args.Length + d);
                        }

                        return result;
                    }
                case LookupWithDefaultInstr i:
                    return Concat(Columns(i.Args), [(i.Default, true, -1)]);
                case LookupInstr i:
                    return Columns(i.Args);
                case LookupWithFallbackInstr i:
                    return Concat(Columns(i.Key), Loose(i.FunctionArgs));
                case InsertInstr i:
                    return Columns(i.Values);
                case InsertIfEqInstr i:
                    return Concat(Columns(i.Values), [(i.Left, true, -1), (i.Right, true, -1)]);
                case RemoveInstr i:
                    return Columns(i.Args);
                case ExternalInstr i:
                    return Loose(i.Args);
                case ExternalWithFallbackInstr i:
                    return Concat(Loose(i.Args), Loose(i.FallbackArgs));
                case AssertEqInstr i:
                    return [(i.Left, true, -1), (i.Right, true, -1)];
                case AssertNeInstr i:
                    return [(i.Left, true, -1), (i.Right, true, -1)];
                case AssertAnyNeInstr i:
                    return Loose(i.Ops);
                case ReadCounterInstr:
                    return [];
                default:
                    throw new ArgumentException($"Unknown instruction kind {instr.Kind}.", nameof(instr));
            }
        }

        /// <summary>
        /// Gets the table an instruction's table operands land in, or <c>null</c> for an instruction without a table.
        /// </summary>
        /// <param name="instr">The instruction.</param>
        /// <returns>The table, or <c>null</c>.</returns>
        public static TableId? TableOf(Instr instr) => instr switch
        {
            LookupOrInsertDefaultInstr i => i.Table,
            LookupWithDefaultInstr i => i.Table,
            LookupInstr i => i.Table,
            LookupWithFallbackInstr i => i.Table,
            InsertInstr i => i.Table,
            InsertIfEqInstr i => i.Table,
            RemoveInstr i => i.Table,
            _ => null,
        };

        /// <summary>
        /// Rebuilds <paramref name="instr"/> with <paramref name="operands"/> (in <see cref="Operands"/> order).
        /// </summary>
        static Instr WithOperands(Instr instr, (QueryEntry Entry, bool IsEntry, int Column)[] operands)
        {
            switch (instr)
            {
                case LookupOrInsertDefaultInstr i:
                    {
                        var keys = Slice(operands, 0, i.Args.Length);
                        var defaults = (WriteVal[])i.Defaults.Clone();
                        for (var d = 0; d < defaults.Length; d++)
                            if (defaults[d].Kind == WriteValKind.Entry)
                                defaults[d] = WriteVal.Of(operands[i.Args.Length + d].Entry);

                        return new LookupOrInsertDefaultInstr(i.Table, keys, defaults, i.Column, i.Destination);
                    }
                case LookupWithDefaultInstr i:
                    return new LookupWithDefaultInstr(i.Table, Slice(operands, 0, i.Args.Length), i.Column, i.Destination, operands[i.Args.Length].Entry);
                case LookupInstr i:
                    return new LookupInstr(i.Table, Slice(operands, 0, i.Args.Length), i.Column, i.Destination);
                case LookupWithFallbackInstr i:
                    return new LookupWithFallbackInstr(i.Table, Slice(operands, 0, i.Key.Length), i.Function, Slice(operands, i.Key.Length, i.FunctionArgs.Length), i.Column, i.Destination);
                case InsertInstr i:
                    return new InsertInstr(i.Table, Slice(operands, 0, i.Values.Length));
                case InsertIfEqInstr i:
                    return new InsertIfEqInstr(i.Table, operands[i.Values.Length].Entry, operands[i.Values.Length + 1].Entry, Slice(operands, 0, i.Values.Length));
                case RemoveInstr i:
                    return new RemoveInstr(i.Table, Slice(operands, 0, i.Args.Length));
                case ExternalInstr i:
                    return new ExternalInstr(i.Function, Slice(operands, 0, i.Args.Length), i.Destination);
                case ExternalWithFallbackInstr i:
                    return new ExternalWithFallbackInstr(i.Function, Slice(operands, 0, i.Args.Length), i.Fallback, Slice(operands, i.Args.Length, i.FallbackArgs.Length), i.Destination);
                case AssertEqInstr:
                    return new AssertEqInstr(operands[0].Entry, operands[1].Entry);
                case AssertNeInstr:
                    return new AssertNeInstr(operands[0].Entry, operands[1].Entry);
                case AssertAnyNeInstr i:
                    return new AssertAnyNeInstr(Slice(operands, 0, i.Ops.Length), i.Divider);
                default:
                    throw new ArgumentException($"Instruction kind {instr.Kind} has no operand to substitute.", nameof(instr));
            }
        }

        static (QueryEntry, bool, int)[] Columns(QueryEntry[] entries)
        {
            var result = new (QueryEntry, bool, int)[entries.Length];
            for (var i = 0; i < entries.Length; i++)
                result[i] = (entries[i], true, i);

            return result;
        }

        static (QueryEntry, bool, int)[] Loose(QueryEntry[] entries)
        {
            var result = new (QueryEntry, bool, int)[entries.Length];
            for (var i = 0; i < entries.Length; i++)
                result[i] = (entries[i], true, -1);

            return result;
        }

        static (QueryEntry, bool, int)[] Concat((QueryEntry, bool, int)[] first, (QueryEntry, bool, int)[] second)
        {
            var result = new (QueryEntry, bool, int)[first.Length + second.Length];
            first.CopyTo(result, 0);
            second.CopyTo(result, first.Length);
            return result;
        }

        static QueryEntry[] Slice((QueryEntry Entry, bool IsEntry, int Column)[] operands, int start, int length)
        {
            var result = new QueryEntry[length];
            for (var i = 0; i < length; i++)
                result[i] = operands[start + i].Entry;

            return result;
        }

    }

}
