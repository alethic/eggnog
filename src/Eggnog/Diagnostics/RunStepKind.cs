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

    }

}
