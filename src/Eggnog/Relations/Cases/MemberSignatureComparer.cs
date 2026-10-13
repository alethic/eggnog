using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Orders the members of one seed group by their signatures for <see cref="ClassPartition.Meet"/>:
    /// lexicographically by the canonical ids each member has in the other live cases, then by the member itself, so
    /// equal signatures are adjacent and the order is deterministic.
    /// </summary>
    /// <remarks>
    /// A struct comparer over a row-major signature matrix, so the sort is specialized and allocates nothing. Element
    /// <c>i</c> of the sorted span is a member's index; its signature is
    /// <c>signatures[i * width .. (i + 1) * width)</c>.
    /// </remarks>
    internal readonly struct MemberSignatureComparer : IComparer<int>
    {

        readonly uint[] signatures;
        readonly uint[] members;
        readonly int width;

        /// <summary>
        /// Initializes a comparer over <paramref name="signatures"/>, <paramref name="width"/> cells per member.
        /// </summary>
        /// <param name="signatures">The signature matrix, row per member.</param>
        /// <param name="members">The members, by index.</param>
        /// <param name="width">The cells of one signature: the number of other live cases.</param>
        public MemberSignatureComparer(uint[] signatures, uint[] members, int width)
        {
            this.signatures = signatures;
            this.members = members;
            this.width = width;
        }

        /// <summary>
        /// Compares the members at indexes <paramref name="x"/> and <paramref name="y"/>. O(width).
        /// </summary>
        /// <param name="x">A member index.</param>
        /// <param name="y">Another member index.</param>
        /// <returns>The order of their signatures, then of the members.</returns>
        public int Compare(int x, int y)
        {
            var a = x * width;
            var b = y * width;
            for (var i = 0; i < width; i++)
            {
                var c = signatures[a + i].CompareTo(signatures[b + i]);
                if (c != 0)
                    return c;
            }

            return members[x].CompareTo(members[y]);
        }

        /// <summary>
        /// Gets whether the members at indexes <paramref name="x"/> and <paramref name="y"/> have equal signatures.
        /// </summary>
        /// <param name="x">A member index.</param>
        /// <param name="y">Another member index.</param>
        /// <returns><c>true</c> if every cell agrees.</returns>
        public bool SameSignature(int x, int y)
        {
            var a = x * width;
            var b = y * width;
            for (var i = 0; i < width; i++)
                if (signatures[a + i] != signatures[b + i])
                    return false;

            return true;
        }

    }

}
