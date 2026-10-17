namespace Eggnog.Relations
{

    /// <summary>
    /// What one lift did. The counts are of writes staged into the parent.
    /// </summary>
    internal sealed class LiftReport
    {

        /// <summary>
        /// Gets the rounds that wrote into the parent.
        /// </summary>
        public int Rounds { get; internal set; }

        /// <summary>
        /// Gets the number of live (not contradictory) cases in the last round examined.
        /// </summary>
        public int LiveCases { get; internal set; }

        /// <summary>
        /// Gets the number of vacuous (contradictory) cases in the last round examined. A case set whose cases were all
        /// disposed as vacuous before its lift reports those.
        /// </summary>
        public int VacuousCases { get; internal set; }

        /// <summary>
        /// Gets whether every case was vacuous, so the parent is contradictory under the cases' coverage: its
        /// contradiction function was written when it has one and the parent did not see it yet.
        /// </summary>
        public bool ParentContradicted { get; internal set; }

        /// <summary>
        /// Gets the unions written: the meet's equalities.
        /// </summary>
        public int Unions { get; internal set; }

        /// <summary>
        /// Gets the rows written with an existing output: relation rows, and constructor and function rows whose id output
        /// one parent id stands for in every case.
        /// </summary>
        public int Rows { get; internal set; }

        /// <summary>
        /// Gets the terms added with a fresh parent id.
        /// </summary>
        public int Terms { get; internal set; }

        /// <summary>
        /// Gets the function rows written with a base value that the cases agree on or that their case join produced.
        /// </summary>
        public int Values { get; internal set; }

        /// <summary>
        /// Gets the candidate keys not examined because their source row stood for more than
        /// <see cref="LiftOptions.CandidateLimit"/> parent keys.
        /// </summary>
        public long Skipped { get; internal set; }

        /// <summary>
        /// Gets whether the lift stopped at <see cref="LiftOptions.RoundLimit"/> while the parent was still changing.
        /// </summary>
        public bool HitRoundLimit { get; internal set; }

        /// <summary>
        /// Gets whether the lift changed the parent. <c>false</c> as well when the memo showed that nothing had changed
        /// since the last lift.
        /// </summary>
        public bool Changed { get; internal set; }

        /// <summary>
        /// Gets whether the memo answered the lift: nothing the last settled lift over these cases read had changed, so
        /// no round ran. A lift that ran and wrote nothing reports the same <see cref="Rounds"/> and
        /// <see cref="Changed"/>, and <c>false</c> here.
        /// </summary>
        public bool Memoized { get; internal set; }

        /// <inheritdoc />
        public override string ToString() => $"LiftReport {{ Rounds = {Rounds}, Live = {LiveCases}, Vacuous = {VacuousCases}, ParentContradicted = {ParentContradicted}, Unions = {Unions}, Rows = {Rows}, Terms = {Terms}, Values = {Values}, Skipped = {Skipped}, HitRoundLimit = {HitRoundLimit}, Changed = {Changed}, Memoized = {Memoized} }}";

    }

}
