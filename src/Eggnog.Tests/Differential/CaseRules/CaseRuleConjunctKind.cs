namespace Eggnog.Tests.Differential.CaseRules
{

    /// <summary>
    /// What a conjunct of a case-rule oracle query says about its path.
    /// </summary>
    public enum CaseRuleConjunctKind
    {

        /// <summary>
        /// <c>path in {values}</c>: an <c>Or</c> of <c>StrEq</c>, or one <c>StrEq</c> for a single value.
        /// </summary>
        In,

        /// <summary>
        /// <c>path != value</c>: a <c>Not</c> of <c>StrEq</c>, which proves the path present.
        /// </summary>
        NotEqual,

        /// <summary>
        /// <c>path &gt; 0</c>: a <c>NumGt</c>, which proves the path present and a number.
        /// </summary>
        Number,

    }

}
