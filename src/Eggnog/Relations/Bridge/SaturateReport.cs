namespace Eggnog.Relations
{

    /// <summary>
    /// What <see cref="EGraphContext.Saturate"/> did: how many iterations it ran, and why it ended.
    /// </summary>
    /// <remarks>
    /// Exactly one of two endings: saturation (an iteration changed nothing, neither by its rules nor by its rebuild),
    /// or the iteration limit (<see cref="HitLimit"/>). An 8-byte value; immutable.
    /// </remarks>
    /// <param name="Iterations">The number of iterations run, the last one included.</param>
    /// <param name="HitLimit">Whether the iteration limit was reached before saturation.</param>
    internal readonly record struct SaturateReport(int Iterations, bool HitLimit)
    {

        /// <summary>
        /// Gets whether the rules saturated: the last iteration changed nothing.
        /// </summary>
        public bool Saturated => HitLimit == false;

    }

}
