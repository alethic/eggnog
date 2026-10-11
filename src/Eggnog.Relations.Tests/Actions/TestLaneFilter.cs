using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="ILaneFilter"/> that asks a delegate, for tests.
    /// </summary>
    struct TestLaneFilter : ILaneFilter
    {

        readonly Func<int, bool> keep;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keep">Decides whether a lane stays.</param>
        public TestLaneFilter(Func<int, bool> keep)
        {
            this.keep = keep;
        }

        /// <inheritdoc />
        public bool Keep(int lane) => keep(lane);

    }

}
