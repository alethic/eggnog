namespace Eggnog.Relations
{

    /// <summary>
    /// The cases of <see cref="RuleTerm"/>: the three argument forms of egglog-bridge's rule macros
    /// (egglog-bridge/src/macros.rs).
    /// </summary>
    internal enum RuleTermKind : byte
    {

        /// <summary>
        /// A variable, by name: an identifier in the macros.
        /// </summary>
        Variable,

        /// <summary>
        /// A constant entry: <c>{ expr }</c> in the macros.
        /// </summary>
        Constant,

        /// <summary>
        /// A function applied to argument terms: <c>(f args...)</c> in the macros.
        /// </summary>
        Call,

    }

}
