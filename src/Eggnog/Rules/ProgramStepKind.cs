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
        /// Keeps the match where two constants are equal: two class constants that are one class in the running context,
        /// a comparison a definition leaves to run time when it cannot drop it as already true (the classes differ where
        /// the rule's constants come from, that scope cannot be read now without racing another thread, or the program is
        /// a query); or two different value constants, which never are, so the program has no match, as egglog's
        /// <c>(= 1 2)</c> in a query.
        /// </summary>
        Equal,

    }

}
