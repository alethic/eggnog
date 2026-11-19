using System.Globalization;
using System.Linq;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// An atom of a <see cref="ScopedProgram"/>: a relation row, or a function's or constructor's value at its keys.
    /// </summary>
    /// <param name="Table">The table's index.</param>
    /// <param name="Keys">The key terms.</param>
    /// <param name="Output">The output term of a function or constructor.</param>
    internal sealed record ScopedAtomSpec(int Table, ScopedTermSpec[] Keys, ScopedTermSpec? Output)
    {

        /// <inheritdoc />
        public override string ToString()
        {
            var name = "T" + Table.ToString(CultureInfo.InvariantCulture);
            var row = "(" + name + (Keys.Length > 0 ? " " : "") + string.Join(" ", Keys.Select(k => k.ToString())) + ")";
            return Output is null ? row : row + " = " + Output;
        }

    }

}
