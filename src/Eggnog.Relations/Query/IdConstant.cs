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
    /// that is canonical in one context may have lost a union in another, so a run must match against the constant's
    /// canonical form where it runs. The rule builders record every one of them, with the atom and column, in
    /// <see cref="RulePlan.IdConstants"/> and <see cref="CachedPlan.IdConstants"/> (and the action program's in
    /// <see cref="ActionInfo.IdConstants"/>, <see cref="ActionIdConstant"/>). A store-backed context canonicalizes them at
    /// every level, the root included (colored-contexts spec §9.8, X19, <see cref="CanonicalConstants"/>); a bare
    /// database has no designated union-find and uses <see cref="Value"/> as written, as egglog does. At a store-backed
    /// root this differs from egglog only for a constant that has lost a union, which egglog's cached plan silently stops
    /// matching (proposed map §8.1 row 25: egglog-bridge/src/lib.rs:1397-1420 builds each rule's plan once, and the
    /// constant entries of core-relations/src/query.rs become fixed <c>EqConst</c> constraints); here it matches the
    /// class.
    /// </para>
    /// <para>
    /// A column holds ids when the table's rebuild canonicalizes it (a <see cref="SortedWritesTable"/>'s
    /// <see cref="SortedWritesTable.RebuildColumns"/>, the bridge's id-typed key and value columns). The union-find
    /// table's columns are never listed: its rows record displaced ids, which canonicalizing would destroy. Constants in
    /// the per-run extra constraints of a cached plan (timestamp bounds) are not recorded.
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
