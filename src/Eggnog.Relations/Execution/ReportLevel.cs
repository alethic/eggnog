namespace Eggnog.Relations
{

    /// <summary>
    /// How much a <see cref="RuleSetReport"/> records about each rule: egglog's <c>ReportLevel</c>
    /// (egglog-reports/src/lib.rs:14-23).
    /// </summary>
    internal enum ReportLevel : byte
    {

        /// <summary>
        /// Only the time each rule's search and apply took, and its match count.
        /// </summary>
        TimeOnly,

        /// <summary>
        /// <see cref="TimeOnly"/> plus the rule's plan.
        /// </summary>
        WithPlan,

        /// <summary>
        /// <see cref="WithPlan"/> plus per-stage statistics. egglog collects none yet (its executor has a TODO for
        /// them), so this reports what <see cref="WithPlan"/> does.
        /// </summary>
        StageInfo,

    }

}
