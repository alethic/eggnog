using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// What one <see cref="CaseSet.Lift"/> did: the counts are of writes made into the parent. Immutable.
    /// </summary>
    public sealed class LiftReport
    {

        /// <summary>
        /// Initializes a report from the engine's.
        /// </summary>
        /// <param name="engine">The engine's report.</param>
        /// <param name="changed">Whether the parent's <see cref="Scope.Version"/> moved.</param>
        internal LiftReport(Relations.LiftReport engine, bool changed)
        {
            Rounds = engine.Rounds;
            LiveCases = engine.LiveCases;
            VacuousCases = engine.VacuousCases;
            ParentContradicted = engine.ParentContradicted;
            Unions = engine.Unions;
            Rows = engine.Rows;
            Terms = engine.Terms;
            Values = engine.Values;
            Skipped = engine.Skipped;
            HitRoundLimit = engine.HitRoundLimit;
            Changed = changed;
        }

        /// <summary>
        /// Gets the rounds that wrote into the parent.
        /// </summary>
        public int Rounds { get; }

        /// <summary>
        /// Gets the number of live (not contradictory) cases the lift read.
        /// </summary>
        public int LiveCases { get; }

        /// <summary>
        /// Gets the number of vacuous (contradictory) cases: they have no model and are left out; the set disposes them
        /// after the lift.
        /// </summary>
        public int VacuousCases { get; }

        /// <summary>
        /// Gets whether every case was vacuous, so that the parent has no model under the cases' coverage: its
        /// <see cref="EGraph.Contradiction"/> was written.
        /// </summary>
        public bool ParentContradicted { get; }

        /// <summary>
        /// Gets the unions written: equalities between the parent's classes that hold in every live case.
        /// </summary>
        public long Unions { get; }

        /// <summary>
        /// Gets the rows written with an existing output: relation rows (a relation lifts as a function whose output is the
        /// unit value), and constructor rows whose class one parent class stands for in every case.
        /// </summary>
        public long Rows { get; }

        /// <summary>
        /// Gets the constructor keys made in the parent: terms every live case holds, with a fresh class whose equalities a
        /// later round lifts.
        /// </summary>
        public long Terms { get; }

        /// <summary>
        /// Gets the function values written: values every live case agrees on, or that the function's case join combined
        /// (<see cref="ValueLattice{T}.CaseJoin"/>).
        /// </summary>
        public long Values { get; }

        /// <summary>
        /// Gets the parent-level candidate rows not examined: each case row that stood for more than
        /// <see cref="LiftOptions.CandidateLimit"/> parent keys adds the number of keys it stood for.
        /// </summary>
        public long Skipped { get; }

        /// <summary>
        /// Gets whether the lift stopped at <see cref="LiftOptions.RoundLimit"/> while the parent was still changing.
        /// </summary>
        public bool HitRoundLimit { get; }

        /// <summary>
        /// Gets whether the parent's <see cref="Scope.Version"/> moved.
        /// </summary>
        public bool Changed { get; }

        /// <summary>
        /// Renders the report: <c>2 live, 1 vacuous, 1 rounds: 0 unions, 2 rows, 0 terms, 1 values, changed</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{LiveCases} live, {VacuousCases} vacuous, {Rounds} rounds: {Unions} unions, {Rows} rows, {Terms} terms, {Values} values{(Skipped > 0 ? $", {Skipped} skipped" : "")}{(HitRoundLimit ? ", round limit" : "")}{(ParentContradicted ? ", parent contradicted" : "")}{(Changed ? ", changed" : ", unchanged")}");

    }

}
