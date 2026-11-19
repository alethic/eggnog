namespace Eggnog
{

    /// <summary>
    /// What a <see cref="RunStep"/> was.
    /// </summary>
    public enum RunStepKind
    {

        /// <summary>
        /// One engine iteration of a ruleset step's rules in a scope.
        /// </summary>
        Iteration,

        /// <summary>
        /// A mirror sync: one iteration of a tag's copy rules in the tagged scope, just before a rule of the run reads
        /// its mirrors (the syncs a query or a freeze makes are not steps). Not counted in
        /// <see cref="RunReport.Iterations"/>; its <see cref="RunStep.Rules"/> is empty.
        /// </summary>
        MirrorSync,

    }

}
