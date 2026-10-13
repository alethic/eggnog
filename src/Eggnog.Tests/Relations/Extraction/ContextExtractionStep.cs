namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One step of a <see cref="ContextExtractionProgram"/>: the level it runs at, what it does, and its operands.
    /// </summary>
    /// <param name="Level">The level of the context it runs in.</param>
    /// <param name="Kind">What it does.</param>
    /// <param name="Freeze">For a child creation, whether the level freezes first.</param>
    /// <param name="Function">For a make, the constructor's index: <c>L0</c>, <c>L1</c>, <c>Num</c>, <c>U</c>,
    /// <c>W</c>, <c>B</c>.</param>
    /// <param name="First">For a make, <c>Num</c>'s literal or the first operand term; for a union, the first
    /// term.</param>
    /// <param name="Second">For a make of <c>B</c>, the second operand term; for a union, the second term.</param>
    readonly record struct ContextExtractionStep(int Level, ContextExtractionStepKind Kind, bool Freeze, int Function, int First, int Second);

}
