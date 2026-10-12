namespace Eggnog
{

    /// <summary>
    /// What a body step of a program does.
    /// </summary>
    internal enum ProgramStepKind
    {

        /// <summary>
        /// Calls a primitive and binds its result to the target, or checks it against a bound or constant target.
        /// </summary>
        Let,

        /// <summary>
        /// Keeps the match where a boolean primitive returns true.
        /// </summary>
        Filter,

        /// <summary>
        /// Keeps the match where two cells differ.
        /// </summary>
        NotEqual,

    }

}
