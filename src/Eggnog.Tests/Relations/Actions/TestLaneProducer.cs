using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="ILaneProducer"/> that asks a delegate, for tests: <c>null</c> fails the lane.
    /// </summary>
    struct TestLaneProducer : ILaneProducer
    {

        readonly Func<int, uint?> produce;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="produce">Produces the value of a lane, or <c>null</c>.</param>
        public TestLaneProducer(Func<int, uint?> produce)
        {
            this.produce = produce;
        }

        /// <inheritdoc />
        public bool TryProduce(int lane, out uint value)
        {
            var result = produce(lane);
            value = result ?? 0;
            return result.HasValue;
        }

    }

}
