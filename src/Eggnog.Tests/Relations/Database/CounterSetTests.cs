using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="CounterSet"/> (egglog's <c>Counters</c>, core-relations/src/free_join/mod.rs:261-284).
    /// </summary>
    public class CounterSetTests
    {

        [Fact]
        public void IncrementReturnsThePreviousValue()
        {
            var counters = new CounterSet();
            var a = counters.Add();
            var b = counters.Add();
            Assert.Equal(new CounterId(0), a);
            Assert.Equal(new CounterId(1), b);
            Assert.Equal(2, counters.Count);

            Assert.Equal(0, counters.Increment(a));
            Assert.Equal(1, counters.Increment(a));
            Assert.Equal(2, counters.Read(a));
            Assert.Equal(0, counters.Read(b));
            for (var i = 0; i < 10; i++)
                counters.Add();

            // Growing kept the values.
            Assert.Equal(2, counters.Read(a));
            Assert.Throws<KeyNotFoundException>(() => counters.Increment(new CounterId(12)));
            Assert.Throws<KeyNotFoundException>(() => counters.Read(new CounterId(-1)));
        }

        [Fact]
        public void ConcurrentIncrementsHandOutEveryValueOnce()
        {
            var counters = new CounterSet();
            var counter = counters.Add();
            var seen = new ConcurrentBag<long>();
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                for (var i = 0; i < 10_000; i++)
                    seen.Add(counters.Increment(counter));
            });

            Assert.Equal(80_000, counters.Read(counter));
            Assert.Equal(Enumerable.Range(0, 80_000).Select(i => (long)i), seen.OrderBy(x => x));
        }

        [Fact]
        public void ClonesSnapshotTheValues()
        {
            var counters = new CounterSet();
            var counter = counters.Add();
            counters.Increment(counter);
            var clone = counters.Clone();
            counters.Increment(counter);

            Assert.Equal(1, clone.Read(counter));
            Assert.Equal(2, counters.Read(counter));
            Assert.Equal(new CounterId(1), clone.Add());
            Assert.Equal(1, counters.Count);
        }

    }

}
