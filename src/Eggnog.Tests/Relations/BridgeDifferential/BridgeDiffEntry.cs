using System.Globalization;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An entry of a bridge differential rule or seed: a variable, or a constant of a sort.
    /// </summary>
    /// <remarks>
    /// In a rule, a variable is an index into the rule's variables (<see cref="BridgeDiffRule.VarSorts"/>). In a seed
    /// (<see cref="BridgeDiffSeed"/>), a "variable" names the e-class an earlier term seed produced, by the seed's
    /// index. A constant is a raw cell: an integer's value, the unit value, or (in atoms only) an id the seeds minted.
    /// </remarks>
    /// <param name="IsVar">Whether the entry is a variable (a seed reference, in a seed).</param>
    /// <param name="Var">The variable's index, or -1.</param>
    /// <param name="Value">The constant's cell, or zero.</param>
    /// <param name="Sort">The sort of the entry.</param>
    readonly record struct BridgeDiffEntry(bool IsVar, int Var, uint Value, int Sort)
    {

        /// <summary>
        /// Creates a variable entry.
        /// </summary>
        /// <param name="index">The variable (or seed) index.</param>
        /// <param name="sort">Its sort.</param>
        /// <returns>The entry.</returns>
        public static BridgeDiffEntry Variable(int index, int sort) => new(true, index, 0, sort);

        /// <summary>
        /// Creates a constant entry.
        /// </summary>
        /// <param name="value">The cell.</param>
        /// <param name="sort">Its sort.</param>
        /// <returns>The entry.</returns>
        public static BridgeDiffEntry Const(uint value, int sort) => new(false, -1, value, sort);

        /// <summary>
        /// Formats the entry in a rule: <c>v3</c>, an integer, <c>()</c>, or <c>#7</c> for an id constant.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString() => IsVar ? "v" + Var.ToString(CultureInfo.InvariantCulture) : FormatConst();

        /// <summary>
        /// Formats the entry in a seed: <c>$s3</c> for the class seed 3 produced, or the constant.
        /// </summary>
        /// <returns>The text.</returns>
        public string FormatSeed() => IsVar ? "$s" + Var.ToString(CultureInfo.InvariantCulture) : FormatConst();

        string FormatConst() => Sort switch
        {
            BridgeDiffSort.Unit => "()",
            BridgeDiffSort.I64 => Value.ToString(CultureInfo.InvariantCulture),
            _ => "#" + Value.ToString(CultureInfo.InvariantCulture),
        };

    }

}
