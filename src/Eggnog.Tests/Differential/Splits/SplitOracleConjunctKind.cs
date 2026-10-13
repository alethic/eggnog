namespace Eggnog.Tests.Differential.Splits
{

    /// <summary>
    /// What a conjunct of a split-oracle query says about its path.
    /// </summary>
    public enum SplitOracleConjunctKind
    {

        /// <summary>
        /// <c>path IN (values)</c>: an <c>Or</c> of <c>StrEq</c>, or one <c>StrEq</c> for a single value.
        /// </summary>
        In,

        /// <summary>
        /// <c>NOT (path = value)</c>.
        /// </summary>
        NotEqual,

        /// <summary>
        /// A UUID comparison, which mentions the path and proves it present.
        /// </summary>
        Uuid,

    }

}
