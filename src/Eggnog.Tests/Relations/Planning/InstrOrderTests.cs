using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="InstrOrder"/>: egglog's <c>InstrOrder</c> (core-relations/src/free_join/execute.rs:2507-2532).
    /// </summary>
    public class InstrOrderTests
    {

        [Fact]
        public void StartsAsTheIdentity()
        {
            var order = new InstrOrder(4);
            Assert.Equal(4, order.Count);
            Assert.Equal([0, 1, 2, 3], order.ToArray());
            Assert.Equal("[0, 1, 2, 3]", order.ToString());
        }

        [Fact]
        public void SwapCloneAndCopy()
        {
            var order = new InstrOrder(3);
            order.Swap(0, 2);
            var clone = order.Clone();
            order.Swap(0, 1);
            Assert.Equal([1, 2, 0], order.ToArray());
            Assert.Equal([2, 1, 0], clone.ToArray());

            var copy = new InstrOrder(1);
            copy.CopyFrom(order);
            Assert.Equal([1, 2, 0], copy.ToArray());
            copy.Reset(2);
            Assert.Equal([0, 1], copy.ToArray());
        }

        [Fact]
        public void HoldsAtMostUShortMaxStages()
        {
            Assert.Equal(InstrOrder.MaxStages, new InstrOrder(InstrOrder.MaxStages).Count);
            Assert.Throws<InvalidOperationException>(() => new InstrOrder(InstrOrder.MaxStages + 1));
        }

        [Fact]
        public void OutOfRangePositionsThrow()
        {
            var order = new InstrOrder(2);
            Assert.Throws<ArgumentOutOfRangeException>(() => order[2]);
            Assert.Throws<ArgumentOutOfRangeException>(() => order.Swap(0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new InstrOrder(-1));
        }

    }

}
