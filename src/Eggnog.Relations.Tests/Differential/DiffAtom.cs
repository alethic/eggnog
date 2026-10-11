using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One atom of a differential query (<see cref="DiffQuery"/>): a table, one entry per column (a variable, a
    /// constant or a wildcard), and extra constraints on the atom's rows, exactly as they are passed to
    /// <see cref="RuleQueryBuilder.AddAtom"/>.
    /// </summary>
    /// <param name="Table">The table's index in the program.</param>
    /// <param name="Entries">One entry per column.</param>
    /// <param name="Constraints">The extra constraints.</param>
    sealed record DiffAtom(int Table, QueryEntry[] Entries, Constraint[] Constraints)
    {

        /// <summary>
        /// Gets a copy with <paramref name="extra"/> appended to the constraints.
        /// </summary>
        public DiffAtom WithConstraints(params Constraint[] extra) => this with { Constraints = Constraints.Concat(extra).ToArray() };

        /// <summary>
        /// Formats the atom with the program's table names.
        /// </summary>
        public string Format(DiffProgram program)
        {
            var entries = string.Join(", ", Entries.Select(e => e.ToString()));
            var where = Constraints.Length > 0 ? " where " + string.Join(", ", Constraints.Select(c => c.ToString())) : "";
            return $"{program.Tables[Table].Name}({entries}){where}";
        }

        /// <inheritdoc />
        public override string ToString() => $"t{Table}({string.Join(", ", Entries.Select(e => e.ToString()))})" + (Constraints.Length > 0 ? " where " + string.Join(", ", Constraints.Select(c => c.ToString())) : "");

        /// <summary>
        /// Gets whether the atom's key columns are all variables or constants, so that the atom's bindings determine
        /// its row.
        /// </summary>
        public bool KeysBound(DiffTable table)
        {
            for (var c = 0; c < table.KeyCount; c++)
                if (Entries[c].IsWildcard)
                    return false;

            return true;
        }

    }

}
