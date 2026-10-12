namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// What a command of a random program does.
    /// </summary>
    internal enum DiffCommandKind
    {

        /// <summary>
        /// Host writes of ground facts, one batch.
        /// </summary>
        Assume,

        /// <summary>
        /// A host-made term, whose class becomes the next handle.
        /// </summary>
        Make,

        /// <summary>
        /// A host union of two handles.
        /// </summary>
        Union,

        /// <summary>
        /// Defines a rule.
        /// </summary>
        DefineRule,

        /// <summary>
        /// Runs a schedule.
        /// </summary>
        Run,

        /// <summary>
        /// Asks a query.
        /// </summary>
        Query,

        /// <summary>
        /// Defines a query after the first command, so that it may hold classes the host made (D39, §6.6.4); it takes the
        /// next query number.
        /// </summary>
        DefineQuery,

    }

}
