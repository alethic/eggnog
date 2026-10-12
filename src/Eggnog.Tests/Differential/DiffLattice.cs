namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// The merge of a random program's function.
    /// </summary>
    internal enum DiffLattice
    {

        /// <summary>
        /// No lattice: written only by host writes, with one value per key.
        /// </summary>
        None,

        /// <summary>
        /// <c>ValueLattice.Max&lt;long&gt;()</c>.
        /// </summary>
        Max,

        /// <summary>
        /// <c>ValueLattice.Intersect&lt;int&gt;()</c>.
        /// </summary>
        Intersect,

    }

}
