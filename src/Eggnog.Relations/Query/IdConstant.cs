namespace Eggnog.Relations
{

    /// <summary>
    /// A constant e-class id an atom of a rule matches against: the atom compares <see cref="Column"/>, a column holding
    /// ids, for equality with <see cref="Value"/>, through a constant entry or an <see cref="ConstraintKind.EqConst"/>
    /// constraint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// egglog bakes such constants into the plan. With colored contexts they are parameters (map §4.3, planner row): an id
    /// that is canonical in one context may have lost a union in another, so a run in a context must match against the
    /// constant's canonical form there. The rule builders record every one of them, with the atom and column, in
    /// <see cref="RulePlan.IdConstants"/> and <see cref="CachedPlan.IdConstants"/>, so a per-context instantiation can
    /// canonicalize them; a root run uses <see cref="Value"/> as written, as egglog does.
    /// </para>
    /// <para>
    /// A column holds ids when the table's rebuild canonicalizes it (a <see cref="SortedWritesTable"/>'s
    /// <see cref="SortedWritesTable.RebuildColumns"/>, the bridge's id-typed key and value columns). The union-find
    /// table's columns are never listed: its rows record displaced ids, which canonicalizing would destroy. Constants in
    /// action operands and in the per-run extra constraints of a cached plan are not recorded.
    /// </para>
    /// </remarks>
    /// <param name="Atom">The atom.</param>
    /// <param name="Column">The id column the atom compares.</param>
    /// <param name="Value">The constant, as written in the rule.</param>
    internal readonly record struct IdConstant(AtomId Atom, ColumnId Column, Value Value)
    {

        /// <inheritdoc />
        public override string ToString() => $"{Atom}.{Column} == {Value}";

    }

}
