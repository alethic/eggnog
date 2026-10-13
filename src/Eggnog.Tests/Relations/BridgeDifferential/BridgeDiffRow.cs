using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A row of a function in a <see cref="BridgeDiffView"/>: its cells, keys then output, and whether it is subsumed.
    /// </summary>
    /// <param name="Values">The cells; owned by the view.</param>
    /// <param name="Subsumed">Whether the row is subsumed.</param>
    readonly record struct BridgeDiffRow(uint[] Values, bool Subsumed)
    {

        /// <summary>
        /// Formats the row as <c>F(k0, k1) = out</c>, with <c>(subsumed)</c> appended when it is.
        /// </summary>
        /// <param name="function">The row's function.</param>
        /// <returns>The text.</returns>
        public string Format(BridgeDiffFunction function)
        {
            return $"{function.Name}({string.Join(", ", Values.Take(function.KeyCount))}) = {Values[function.KeyCount]}{(Subsumed ? " (subsumed)" : "")}";
        }

    }

}
