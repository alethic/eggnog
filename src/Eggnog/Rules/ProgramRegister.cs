namespace Eggnog
{

    /// <summary>
    /// A register of a program: its column type, its name (a declared variable's, or a generated one) and where it comes
    /// from.
    /// </summary>
    /// <param name="Type">The column type of its cells.</param>
    /// <param name="Name">The name, for the engine's variables, plans and messages.</param>
    /// <param name="Kind">Where it comes from.</param>
    internal readonly record struct ProgramRegister(ColumnType Type, string Name, RegisterKind Kind);

}
