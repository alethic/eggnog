using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A split demand for the driver tests, from a predicate over the request; counts its calls.
    /// </summary>
    sealed class TestSplitDemand : ISplitDemand
    {

        readonly Func<SplitRequest, bool> wants;

        /// <summary>
        /// Initializes a demand that takes the requests <paramref name="wants"/> accepts.
        /// </summary>
        public TestSplitDemand(Func<SplitRequest, bool> wants)
        {
            this.wants = wants;
        }

        /// <summary>
        /// Gets the number of requests asked about.
        /// </summary>
        public int Calls { get; private set; }

        /// <inheritdoc />
        public bool Wants(EGraphContext scope, in SplitRequest request)
        {
            Calls++;
            return wants(request);
        }

    }

}
