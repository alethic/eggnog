using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="ILaneVisitor"/> that forwards each lane to a delegate, for tests.
    /// </summary>
    struct TestLaneVisitor : ILaneVisitor
    {

        readonly Action<int> visit;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="visit">Called with each lane.</param>
        public TestLaneVisitor(Action<int> visit)
        {
            this.visit = visit;
        }

        /// <inheritdoc />
        public void Visit(int lane) => visit(lane);

    }

}
