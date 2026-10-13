namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The sorts of a bridge differential program (<see cref="BridgeDiffProgram"/>), as plain integers: a non-negative
    /// number is an eq sort (an e-class id column, <see cref="ColumnTy.Id"/>), named <c>A</c>, <c>B</c>, ...; the two
    /// negative constants are the base sorts the programs use, 64-bit integers and the unit value.
    /// </summary>
    /// <remarks>
    /// The bridge itself has no sorts: every eq sort is an id column. Programs keep them so that their rules are
    /// well-sorted, as the frontend's are (unions and joins only between ids of one sort), and so that their listings
    /// read like egglog. Integers stay small and non-negative, so that the engine stores them inline
    /// (<see cref="Int64Codec"/>) and a value's representation is the value itself, in every engine and in the oracle.
    /// </remarks>
    static class BridgeDiffSort
    {

        /// <summary>
        /// The sort of 64-bit integers, stored inline as their value.
        /// </summary>
        public const int I64 = -1;

        /// <summary>
        /// The sort of the unit value, the result of the filter primitives. Relations are written with it as their
        /// output, as egglog writes none; their cells are nonetheless ids
        /// (<see cref="BridgeDiffFunction.IsIdColumn"/>), and a variable bound to a relation's output holds one.
        /// </summary>
        public const int Unit = -2;

        /// <summary>
        /// Gets whether <paramref name="sort"/> is an eq sort, whose values are e-class ids.
        /// </summary>
        /// <param name="sort">The sort.</param>
        /// <returns><c>true</c> for an eq sort.</returns>
        public static bool IsEq(int sort) => sort >= 0;

        /// <summary>
        /// Gets the name of <paramref name="sort"/>: <c>i64</c>, <c>Unit</c>, or a capital letter.
        /// </summary>
        /// <param name="sort">The sort.</param>
        /// <returns>The name.</returns>
        public static string Name(int sort) => sort switch
        {
            I64 => "i64",
            Unit => "Unit",
            _ => ((char)('A' + sort)).ToString(),
        };

    }

}
