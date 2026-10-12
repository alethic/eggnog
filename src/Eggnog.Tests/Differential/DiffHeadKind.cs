namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// What a head item of a random rule is.
    /// </summary>
    internal enum DiffHeadKind
    {

        /// <summary>
        /// A head fact: a row, a function write, a constructor write or a union.
        /// </summary>
        Then,

        /// <summary>
        /// A head variable bound by get-or-add of a constructor term.
        /// </summary>
        Make,

        /// <summary>
        /// A rewrite: a fresh class variable equal to the left side, unioned with the right side.
        /// </summary>
        Rewrite,

    }

}
