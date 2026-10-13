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

        /// <summary>
        /// Keeps the match where two class constants are one class in the running context: a comparison a definition
        /// leaves to run time when it cannot drop it as already true (the classes differ where the rule's constants come
        /// from, that scope cannot be read now without racing another thread, or the program is a query).
        /// </summary>
        Equal,

    }

}
