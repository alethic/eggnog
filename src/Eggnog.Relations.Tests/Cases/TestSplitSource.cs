using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A split source for the driver tests: its requests and assumptions are lambdas; it logs its calls and counts those
    /// made while the calling thread held the scope's thread domain (which the driver must never do, rule-layer E8).
    /// </summary>
    sealed class TestSplitSource : ISplitSource
    {

        readonly int index;
        readonly Func<EGraphContext, IEnumerable<(uint[] Key, int Cases)>> find;
        readonly Action<EGraphContext, int, uint[]> assume;

        /// <summary>
        /// Initializes source <paramref name="index"/> of its driver.
        /// </summary>
        /// <param name="index">The source's index in the driver's list.</param>
        /// <param name="find">The requests for a scope: key cells and case count.</param>
        /// <param name="assume">Makes a case assume case <c>i</c> of the request with the given key cells.</param>
        public TestSplitSource(int index, Func<EGraphContext, IEnumerable<(uint[] Key, int Cases)>> find, Action<EGraphContext, int, uint[]> assume)
        {
            this.index = index;
            this.find = find;
            this.assume = assume;
        }

        /// <summary>
        /// Gets or sets a log the source appends its calls to.
        /// </summary>
        public List<string>? Log { get; set; }

        /// <summary>
        /// Gets the number of calls made while the thread held the scope's (or the case's) domain.
        /// </summary>
        public int CallsWithDomainHeld { get; private set; }

        /// <inheritdoc />
        public void FindRequests(EGraphContext scope, List<SplitRequest> requests, SplitReport report)
        {
            if (scope.Context.Domain.Depth > 0)
                CallsWithDomainHeld++;

            Log?.Add($"find s{index} d{scope.Depth}");
            foreach (var (key, cases) in find(scope))
            {
                if (cases == 0)
                {
                    report.SplitsSkipped++;
                    continue;
                }

                requests.Add(new SplitRequest(new SplitKey(index, key), cases, key));
            }
        }

        /// <inheritdoc />
        public void Assume(EGraphContext @case, in SplitRequest request, int caseIndex)
        {
            if (@case.Context.Domain.Depth > 0)
                CallsWithDomainHeld++;

            Log?.Add($"assume s{index} {caseIndex}");
            assume(@case, caseIndex, (uint[])request.Payload!);
        }

    }

}
