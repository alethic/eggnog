namespace Eggnog
{

    /// <summary>
    /// What a table's rows mean.
    /// </summary>
    public enum TableRole
    {

        /// <summary>
        /// Constructor rows: terms.
        /// </summary>
        Term,

        /// <summary>
        /// Claims: lifted by default; erasing rules must read them scoped.
        /// </summary>
        Fact,

        /// <summary>
        /// Derived from terms only: never lifted by default; a rule that writes one may read only term and analysis
        /// tables, and no scoped view, so an analysis depends on terms alone.
        /// </summary>
        Analysis,

    }

}
