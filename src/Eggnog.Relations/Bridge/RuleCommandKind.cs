namespace Eggnog.Relations
{

    /// <summary>
    /// The cases of <see cref="RuleCommand"/>: the right-hand-side commands of egglog-bridge's rule macros
    /// (egglog-bridge/src/macros.rs:50-72).
    /// </summary>
    public enum RuleCommandKind : byte
    {

        /// <summary>
        /// <c>(let name term)</c>: name a looked-up term.
        /// </summary>
        Let,

        /// <summary>
        /// <c>(set (f args...) value)</c>: write a row.
        /// </summary>
        Set,

        /// <summary>
        /// <c>(union left right)</c>: union two terms.
        /// </summary>
        Union,

    }

}
