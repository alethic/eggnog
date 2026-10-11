using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the lanes in which two operands are equal, or the lanes in which they differ: egglog's <c>assert_impl</c>
    /// for <c>AssertEq</c> and <c>AssertNe</c> (core-relations/src/action/mod.rs:617-640, 873-874) for every operand
    /// shape but two constants.
    /// </summary>
    internal readonly struct EqualityFilter : ILaneFilter
    {

        readonly ArgSource left;
        readonly ArgSource right;
        readonly bool keepEqual;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="left">The first operand.</param>
        /// <param name="right">The second operand.</param>
        /// <param name="keepEqual"><c>true</c> to keep equal lanes (<c>AssertEq</c>), <c>false</c> to keep differing ones.</param>
        public EqualityFilter(ArgSource left, ArgSource right, bool keepEqual)
        {
            this.left = left;
            this.right = right;
            this.keepEqual = keepEqual;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Keep(int lane) => (left.Get(lane) == right.Get(lane)) == keepEqual;

    }

}
