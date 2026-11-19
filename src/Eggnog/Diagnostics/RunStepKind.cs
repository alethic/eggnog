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
        /// One iteration of a step's rules in a scope: an engine iteration of a ruleset step's rules, or a step of
        /// <see cref="Schedule.RunWith"/>, whose query and action rules run in two.
        /// </summary>
        Iteration = 0,

    }

}
