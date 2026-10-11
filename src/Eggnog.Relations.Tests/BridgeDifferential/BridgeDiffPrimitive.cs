namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The primitives bridge differential rules call: two filters, which produce the unit value or nothing, and three
    /// operations on integers. See <see cref="BridgeDiffPrimitives"/> for their definitions.
    /// </summary>
    enum BridgeDiffPrimitive
    {

        /// <summary>
        /// <c>(!= a b)</c>: the unit value when the two cells differ, else nothing. On ids, the cells are canonical, so the
        /// filter keeps matches whose classes differ, which no renaming of ids changes.
        /// </summary>
        NotEqual,

        /// <summary>
        /// <c>(&lt; a b)</c> on integers: the unit value when <c>a &lt; b</c>, else nothing.
        /// </summary>
        Less,

        /// <summary>
        /// <c>(+ a b)</c> on integers, saturating at <see cref="BridgeDiffPrimitives.Cap"/> so that values stay inline.
        /// </summary>
        Add,

        /// <summary>
        /// <c>(min a b)</c> on integers; also the merge of <see cref="BridgeDiffFunctionKind.Min"/> functions.
        /// </summary>
        Min,

        /// <summary>
        /// <c>(max a b)</c> on integers; also the merge of <see cref="BridgeDiffFunctionKind.Max"/> functions.
        /// </summary>
        Max,

    }

}
