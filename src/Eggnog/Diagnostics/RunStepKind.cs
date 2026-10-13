namespace Eggnog
{

    /// <summary>
    /// What a <see cref="RunStep"/> was.
    /// </summary>
    public enum RunStepKind
    {

        /// <summary>
        /// One engine iteration of a ruleset step's rules in a scope (§6.5.1).
        /// </summary>
        Iteration,

        /// <summary>
        /// A mirror sync (D48, §6.8): one iteration of a tag's copy rules in the tagged scope, just before a rule, query
        /// or freeze read its mirrors. Not counted in <see cref="RunReport.Iterations"/>; its <see cref="RunStep.Rules"/>
        /// is empty.
        /// </summary>
        MirrorSync,

        /// <summary>
        /// A split step (R3, §6.9.3): its split rules' matches found, the cases of the requests taken opened
        /// (<see cref="RunStep.CasesOpened"/>), the in-case schedule run in them and their lifts made. Its
        /// <see cref="RunStep.Rules"/> is empty; the iterations in the cases and the lifts are steps of their own, recorded
        /// before it.
        /// </summary>
        SplitStep,

        /// <summary>
        /// A lift (R3, §6.9): what every live case of one split proves, written into the split scope by a split step.
        /// Recorded between the iterations of the cases, with the split scope as its <see cref="RunStep.Scope"/>.
        /// </summary>
        Lift,

    }

}
