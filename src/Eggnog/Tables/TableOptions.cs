namespace Eggnog
{

    /// <summary>
    /// How a table is defined, beyond its name and columns.
    /// </summary>
    public sealed class TableOptions
    {

        /// <summary>
        /// Gets the role of a relation or function: <see cref="TableRole.Fact"/> (the default) or
        /// <see cref="TableRole.Analysis"/>; <see cref="TableRole.Term"/> is rejected. Ignored for constructors, which are
        /// terms.
        /// </summary>
        public TableRole Role { get; init; } = TableRole.Fact;

        /// <summary>
        /// Gets whether lifts copy the table into a case's parent (R3). <c>null</c>: facts yes, analyses no, constructors
        /// yes (term lift).
        /// </summary>
        public bool? Lift { get; init; }

        /// <summary>
        /// Gets, for a constructor, whether extraction may use its rows; <c>false</c> makes extraction skip them.
        /// </summary>
        public bool Extractable { get; init; } = true;

    }

}
