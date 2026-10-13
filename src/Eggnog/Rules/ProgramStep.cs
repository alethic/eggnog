namespace Eggnog
{

    /// <summary>
    /// A body step of a program, run after the join on every match, in placement order: rounds over the steps in
    /// declaration order, each placing every step whose inputs the atoms or the steps of earlier rounds bind.
    /// </summary>
    /// <param name="Kind">What the step does.</param>
    /// <param name="Primitive">The primitive of a let or filter.</param>
    /// <param name="Arguments">The arguments; for a not-equal, the two sides.</param>
    /// <param name="Target">A let's target: an unbound register it binds, or a bound register or constant it checks.</param>
    internal sealed record ProgramStep(ProgramStepKind Kind, Primitive? Primitive, Operand[] Arguments, Operand Target);

}
