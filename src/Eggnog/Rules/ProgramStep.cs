namespace Eggnog
{

    /// <summary>
    /// A body step of a program, run after the join on every match, in placement order (§6.3, step 4).
    /// </summary>
    /// <param name="Kind">What the step does.</param>
    /// <param name="Primitive">The primitive of a let or filter.</param>
    /// <param name="Arguments">The arguments; for a not-equal, the two sides.</param>
    /// <param name="Target">A let's target: an unbound register it binds, or a bound register or constant it checks.</param>
    internal sealed record ProgramStep(ProgramStepKind Kind, Primitive? Primitive, Operand[] Arguments, Operand Target);

}
