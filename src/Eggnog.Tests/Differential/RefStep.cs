namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A step of a reference body, run after the join once its inputs are bound.
    /// </summary>
    /// <param name="Kind">What it does.</param>
    /// <param name="Primitive">A let's or filter's primitive.</param>
    /// <param name="Arguments">A let's or filter's arguments, or the two sides of an equality or disequality.</param>
    /// <param name="Target">A let's target.</param>
    internal sealed record RefStep(RefStepKind Kind, DiffPrimitive? Primitive, RefSlot[] Arguments, RefSlot Target);

}
