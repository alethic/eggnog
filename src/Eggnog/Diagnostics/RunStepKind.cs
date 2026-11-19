namespace Eggnog
{

    /// <summary>
    /// What a <see cref="RunStep"/> was.
    /// </summary>
    /// <remarks>
    /// Each kind's number is fixed, so a kind stored or sent as an integer reads back as the same kind.
    /// </remarks>
    public enum RunStepKind
    {

        /// <summary>
        /// One engine iteration of a ruleset step's rules in a scope.
        /// </summary>
        Iteration = 0,

    }

}
