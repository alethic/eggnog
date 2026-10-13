namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A table of a <see cref="ScopedProgram"/>. Relations and functions are value-only, so they may be read scoped.
    /// </summary>
    /// <param name="Name">The table's name.</param>
    /// <param name="Kind">The table's kind.</param>
    /// <param name="StringKeys">Per key column: a <c>String</c> key, else an <c>i64</c>.</param>
    /// <param name="Max">For a function: merged by max, else by min.</param>
    internal sealed record ScopedTableSpec(string Name, ScopedTableKind Kind, bool[] StringKeys, bool Max)
    {

        /// <summary>
        /// Gets whether the table may be read scoped: a relation or a function.
        /// </summary>
        public bool IsValueOnly => Kind is ScopedTableKind.Relation or ScopedTableKind.Function;

        /// <summary>
        /// Gets whether the table has an output column: a function or a constructor.
        /// </summary>
        public bool HasOutput => Kind is ScopedTableKind.Function or ScopedTableKind.Constructor;

    }

}
