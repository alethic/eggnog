namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// What a schedule node of a random program is.
    /// </summary>
    internal enum DiffScheduleKind
    {

        /// <summary>
        /// One iteration of rulesets.
        /// </summary>
        Run,

        /// <summary>
        /// Passes of the steps until one changes nothing.
        /// </summary>
        Saturate,

        /// <summary>
        /// Up to a number of passes of the steps.
        /// </summary>
        Repeat,

        /// <summary>
        /// The steps once each.
        /// </summary>
        Sequence,

        /// <summary>
        /// The body unless every fact of the condition holds.
        /// </summary>
        Until,

    }

}
