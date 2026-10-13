namespace Eggnog.Relations
{

    /// <summary>
    /// Demand-driven splitting (colored-contexts spec §11.3, L5; rule-layer E8, D43): decides, before any case exists,
    /// whether a split is worth taking, for example only when it unblocks a rule. The rule layer passes none: its split
    /// bodies are their own demand.
    /// </summary>
    internal interface ISplitDemand
    {

        /// <summary>
        /// Gets whether <paramref name="scope"/> should take <paramref name="request"/>. Called without the scope's thread
        /// domain held.
        /// </summary>
        /// <param name="scope">The scope, clean.</param>
        /// <param name="request">The request.</param>
        /// <returns><c>true</c> to open its cases.</returns>
        bool Wants(EGraphContext scope, in SplitRequest request);

    }

}
