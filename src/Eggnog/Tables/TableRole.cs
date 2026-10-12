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
        /// Claims: lifted by default; erasing rules must read them scoped (R2).
        /// </summary>
        Fact,

        /// <summary>
        /// Derived from terms only: never lifted by default; rules that write them may read only term and analysis
        /// tables (invariant A).
        /// </summary>
        Analysis,

    }

}
