using System;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    public class MatchCounterTests
    {

        [Fact]
        public void CountsPerActionAcrossThreads()
        {
            var counter = new MatchCounter(3);
            Parallel.For(0, 1000, i => counter.Add(i % 3, 2));
            Assert.Equal(668, counter.Read(0));
            Assert.Equal(666, counter.Read(1));
            Assert.Equal(666, counter.Read(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => counter.Add(3, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => counter.Read(-1));
        }

    }

}
