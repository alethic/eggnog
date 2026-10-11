namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A left-hand-side primitive of a bridge differential rule (<see cref="EGraphRuleBuilder.QueryPrim"/>): a call on two
    /// entries whose result is a variable, bound by the call when nothing bound it before, or else compared with it, like
    /// a constant result.
    /// </summary>
    /// <remarks>
    /// The bridge evaluates it after the join, on every match of every variant, and drops the matches where the call
    /// produces nothing or its result differs from a bound result.
    /// </remarks>
    /// <param name="Primitive">The primitive.</param>
    /// <param name="Left">The first argument.</param>
    /// <param name="Right">The second argument.</param>
    /// <param name="Result">The result entry.</param>
    sealed record BridgeDiffPrim(BridgeDiffPrimitive Primitive, BridgeDiffEntry Left, BridgeDiffEntry Right, BridgeDiffEntry Result)
    {

        /// <summary>
        /// Formats the primitive egglog-style: <c>(= v5 (+ v3 1))</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString() => $"(= {Result} ({BridgeDiffPrimitives.Name(Primitive)} {Left} {Right}))";

    }

}
