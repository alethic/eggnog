using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One instruction of a differential rule's action, after the recorder call (<see cref="DiffQuery"/>).
    /// </summary>
    /// <remarks>
    /// Operands are query entries over the query's variables, constants, the results of earlier lookups (variables
    /// numbered from <see cref="DiffQuery.VariableCount"/> up, in lookup order), or <see cref="EpochMarker"/>, which
    /// stands for the epoch of the run and is substituted when the rule is built.
    /// </remarks>
    /// <param name="Kind">The instruction.</param>
    /// <param name="Table">The table written or read, or -1 for assertions.</param>
    /// <param name="Entries">The row (insert), the key (remove, lookup) or the two operands (assertions).</param>
    /// <param name="Column">The column a lookup binds, or -1.</param>
    /// <param name="Result">The variable a lookup defines, or -1.</param>
    sealed record DiffAction(DiffActionKind Kind, int Table, QueryEntry[] Entries, int Column, int Result)
    {

        /// <summary>
        /// The constant that stands for the epoch of the run: rules stamp the rows they write with it.
        /// </summary>
        public const uint EpochMarker = 0xFFFF_FFFE;

        /// <summary>
        /// Gets the epoch marker as an entry.
        /// </summary>
        public static QueryEntry Epoch => QueryEntry.Const(new Value(EpochMarker));

        /// <summary>
        /// Formats the instruction with the program's table names.
        /// </summary>
        public string Format(DiffProgram program)
        {
            var args = string.Join(", ", Entries.Select(Show));
            return Kind switch
            {
                DiffActionKind.Insert => program.Tables[Table].IsUnionFind ? $"union {program.Tables[Table].Name}({args})" : $"insert {program.Tables[Table].Name}({args})",
                DiffActionKind.Remove => $"remove {program.Tables[Table].Name}({args})",
                DiffActionKind.Lookup => $"v{Result} = lookup {program.Tables[Table].Name}({args}).c{Column}",
                DiffActionKind.AssertEq => $"assert {Show(Entries[0])} == {Show(Entries[1])}",
                _ => $"assert {Show(Entries[0])} != {Show(Entries[1])}",
            };
        }

        /// <summary>
        /// Formats an operand, showing the epoch marker as <c>@</c>.
        /// </summary>
        public static string Show(QueryEntry e) => e.IsVariable == false && e.Constant.Rep == EpochMarker ? "@" : e.ToString();

    }

}
