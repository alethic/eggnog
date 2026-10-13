namespace Eggnog.Relations
{

    /// <summary>
    /// What <see cref="EGraphContext.Saturate"/> did (colored-contexts spec §10.3): how many iterations it ran, and why
    /// it ended.
    /// </summary>
    /// <remarks>
    /// Exactly one of three endings: saturation (an iteration changed nothing, neither by its rules nor by its rebuild),
    /// a contradiction that stopped the context (<see cref="Stopped"/>), or the iteration limit (<see cref="HitLimit"/>).
    /// A 12-byte value; immutable.
    /// </remarks>
    /// <param name="Iterations">The number of iterations run, the last one included.</param>
    /// <param name="Stopped">Whether a contradiction stopped the context (§6.9).</param>
    /// <param name="HitLimit">Whether the iteration limit was reached before saturation.</param>
    internal readonly record struct SaturateReport(int Iterations, bool Stopped, bool HitLimit)
    {

        /// <summary>
        /// Gets whether the rules saturated: the last iteration changed nothing.
        /// </summary>
        public bool Saturated => Stopped == false && HitLimit == false;

    }

}
