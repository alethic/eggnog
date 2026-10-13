using System.Globalization;
using System.Linq;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// An atom of a <see cref="ScopedProgram"/>: a relation row, or a function's or constructor's value at its keys;
    /// in a body optionally read in the scope a tag resolves to.
    /// </summary>
    /// <param name="Table">The table's index.</param>
    /// <param name="Keys">The key terms.</param>
    /// <param name="Output">The output term of a function or constructor.</param>
    /// <param name="Tag">The tag a scoped atom reads; -1 for the running scope.</param>
    internal sealed record ScopedAtomSpec(int Table, ScopedTermSpec[] Keys, ScopedTermSpec? Output, int Tag)
    {

        /// <inheritdoc />
        public override string ToString()
        {
            var name = "T" + Table.ToString(CultureInfo.InvariantCulture) + (Tag >= 0 ? "@t" + Tag.ToString(CultureInfo.InvariantCulture) : "");
            var row = "(" + name + (Keys.Length > 0 ? " " : "") + string.Join(" ", Keys.Select(k => k.ToString())) + ")";
            return Output is null ? row : row + " = " + Output;
        }

    }

}
