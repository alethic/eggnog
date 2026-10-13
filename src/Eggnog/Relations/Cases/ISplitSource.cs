using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Where splits come from (colored-contexts spec §11.3; rule-layer E8, §6.9.2): a source finds the splits a scope
    /// could take and, once the driver opens a split's cases, makes each case assume its part.
    /// </summary>
    /// <remarks>
    /// The driver calls a source without holding the scope's thread domain, so the source may run queries, write and create
    /// contexts. Coverage is the source's obligation: the cases of one request must cover the scope (every model of the
    /// scope is a model of some case), or the lift is unsound.
    /// </remarks>
    internal interface ISplitSource
    {

        /// <summary>
        /// Appends the splits <paramref name="scope"/> could take now to <paramref name="requests"/>, each with a key whose
        /// <see cref="SplitKey.Source"/> is this source's index in the driver. A request the source declines itself (for a
        /// rule split, an empty set) is counted in <see cref="SplitReport.SplitsSkipped"/>.
        /// </summary>
        /// <param name="scope">The scope, clean.</param>
        /// <param name="requests">Receives the requests.</param>
        /// <param name="report">The step's report.</param>
        void FindRequests(EGraphContext scope, List<SplitRequest> requests, SplitReport report);

        /// <summary>
        /// Makes <paramref name="case"/> assume case <paramref name="caseIndex"/> of <paramref name="request"/>: writes its
        /// assumptions (for a rule split, the facts that bind the set's element) and flushes them.
        /// </summary>
        /// <param name="case">The new case, a child of the scope.</param>
        /// <param name="request">The request, as <see cref="FindRequests"/> made it.</param>
        /// <param name="caseIndex">The case's index, from 0 to the request's case count − 1.</param>
        void Assume(EGraphContext @case, in SplitRequest request, int caseIndex);

    }

}
