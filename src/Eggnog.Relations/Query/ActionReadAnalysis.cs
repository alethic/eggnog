using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Decides whether a rule's action program is <i>query-determined</i> (colored-contexts spec §10.2, X41): whether what
    /// it writes depends only on the match and on the running context's equalities, never on table contents a descendant
    /// context can change. Only a rule whose actions are query-determined may inherit a semi-naive watermark from an
    /// ancestor (I31's precondition, enforced rather than assumed).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A program is query-determined iff every instruction is one of: <see cref="InstrKind.Insert"/>,
    /// <see cref="InstrKind.InsertIfEq"/>, <see cref="InstrKind.Remove"/>, <see cref="InstrKind.AssertEq"/>,
    /// <see cref="InstrKind.AssertNe"/>, <see cref="InstrKind.AssertAnyNe"/> or <see cref="InstrKind.ReadCounter"/>; a
    /// <see cref="InstrKind.LookupOrInsertDefault"/> of a key or the union column of a shared constructor (its row
    /// exists after the ancestor's run, and its value changes only by unions, which a descendant sees through its find);
    /// any lookup of the store's union-find (canonicalization); or an <see cref="InstrKind.External"/> or
    /// <see cref="InstrKind.ExternalWithFallback"/> whose functions all have <see cref="ExternalFunction.ReadsTables"/>
    /// <c>false</c>.
    /// </para>
    /// <para>
    /// Everything else reads a row a descendant can change, and an inherited watermark would never re-fire the old match:
    /// <see cref="InstrKind.Lookup"/>, <see cref="InstrKind.LookupWithDefault"/> and
    /// <see cref="InstrKind.LookupWithFallback"/> of any other table (a lattice value raised, a row added), a
    /// lookup-or-insert of a table whose merge is not a union or of a value column other than the union column (a bridge
    /// subsumption cell), a context-private table (descendants never see an ancestor's rows of it), and an external that
    /// reads tables or is not registered. Conservative by construction: an unknown instruction is not query-determined.
    /// </para>
    /// <para>
    /// Cold path: once per rule, when it is planned. O(instructions), without allocation.
    /// </para>
    /// </remarks>
    internal static class ActionReadAnalysis
    {

        /// <summary>
        /// Gets whether <paramref name="actions"/> is query-determined (see the type remarks).
        /// </summary>
        /// <param name="actions">The rule's lowered actions.</param>
        /// <param name="schema">The store's schema: which tables are constructors, private, or the union-find.</param>
        /// <param name="externals">The external functions the actions may call.</param>
        /// <returns><c>true</c> if every instruction is query-determined.</returns>
        public static bool IsQueryDetermined(ActionInfo actions, LayeredSchema schema, ExternalFunctionRegistry externals)
        {
            ArgumentNullException.ThrowIfNull(actions);
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(externals);
            foreach (var instr in actions.Instrs)
                if (IsQueryDetermined(instr, schema, externals) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Gets whether one instruction is query-determined (see the type remarks).
        /// </summary>
        /// <param name="instr">The instruction.</param>
        /// <param name="schema">The store's schema.</param>
        /// <param name="externals">The registered external functions.</param>
        /// <returns><c>true</c> if what the instruction does depends only on the match and the context's equalities.</returns>
        public static bool IsQueryDetermined(Instr instr, LayeredSchema schema, ExternalFunctionRegistry externals)
        {
            ArgumentNullException.ThrowIfNull(instr);
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(externals);
            switch (instr)
            {
                case InsertInstr:
                case InsertIfEqInstr:
                case RemoveInstr:
                case AssertEqInstr:
                case AssertNeInstr:
                case AssertAnyNeInstr:
                case ReadCounterInstr:
                    return true;
                case LookupOrInsertDefaultInstr i:
                    return i.Table == schema.UnionFind || ReadsConstructorUnionCell(schema, i.Table, i.Column);
                case LookupInstr i:
                    return i.Table == schema.UnionFind;
                case LookupWithDefaultInstr i:
                    return i.Table == schema.UnionFind;
                case LookupWithFallbackInstr i:
                    return i.Table == schema.UnionFind && IsPure(externals, i.Function);
                case ExternalInstr i:
                    return IsPure(externals, i.Function);
                case ExternalWithFallbackInstr i:
                    return IsPure(externals, i.Function) && IsPure(externals, i.Fallback);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Gets whether a lookup-or-insert of <paramref name="column"/> in <paramref name="table"/> reads only what unions
        /// change: the table is a shared constructor and the column is a key or its union column.
        /// </summary>
        static bool ReadsConstructorUnionCell(LayeredSchema schema, TableId table, ColumnId column)
        {
            if (schema[table] is not { IsContextPrivate: false } s)
                return false;

            var union = s.ConstructorValueColumn;
            return union >= 0 && (column.Rep < s.KeyCount || column.Rep == union);
        }

        /// <summary>
        /// Gets whether <paramref name="function"/> is registered and reads no table.
        /// </summary>
        static bool IsPure(ExternalFunctionRegistry externals, ExternalFunctionId function)
        {
            return externals.TryGet(function, out var f) && f.ReadsTables == false;
        }

    }

}
