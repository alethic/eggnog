using System;
using System.Collections.Generic;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests that <see cref="DependencyGraph.LevelOf"/> is safe while tables are added, as a store may add tables at
    /// any time: child contexts merge concurrently, reading the shared graph, while the store adds tables to a frozen
    /// root.
    /// </summary>
    public class DependencyGraphConcurrencyTests
    {

        static TableId T(int i) => new(i);

        /// <summary>
        /// The level the test's graph gives table <paramref name="i"/>: tables read the table three below them, so
        /// levels climb every third table.
        /// </summary>
        static int ExpectedLevel(int i) => i / 3;

        [Fact]
        public void LevelsReadDuringAdditionsAreAlwaysRight()
        {
            const int tables = 3_000;
            var graph = new DependencyGraph();
            var published = 0;
            var stop = 0;
            using var start = new ManualResetEventSlim();
            var readers = ConcurrencyTestThread.StartMany(8, r =>
            {
                var rng = new Random(r);
                start.Wait();
                while (Volatile.Read(ref stop) == 0)
                {
                    // Every table published so far answers with its level; the next ones are unregistered or right.
                    var known = Volatile.Read(ref published);
                    if (known == 0)
                        continue;

                    var i = rng.Next(known);
                    Assert.Equal(ExpectedLevel(i), graph.LevelOf(T(i)));
                    Assert.True(graph.Contains(T(i)));
                    var ahead = known + rng.Next(64);
                    if (graph.Contains(T(ahead)))
                        Assert.Equal(ExpectedLevel(ahead), graph.LevelOf(T(ahead)));
                }
            });

            start.Set();
            try
            {
                for (var i = 0; i < tables; i++)
                {
                    if (i < 3)
                        graph.AddTable(T(i), [], [T(i + 1)]);
                    else
                        graph.AddTable(T(i), [T(i - 3)], []);

                    Volatile.Write(ref published, i + 1);
                }
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                ConcurrencyTestThread.JoinAll(readers);
            }

            for (var i = 0; i < tables; i++)
                Assert.Equal(ExpectedLevel(i), graph.LevelOf(T(i)));

            Assert.Throws<KeyNotFoundException>(() => graph.LevelOf(T(tables)));
        }

        [Fact]
        public void SparseRegistrationsAndClonesKeepTheirLevels()
        {
            var graph = new DependencyGraph();
            graph.AddTable(T(5), [], []);
            graph.AddTable(T(40), [T(5)], []);
            graph.AddTable(T(2), [T(40)], []);
            Assert.Equal(0, graph.LevelOf(T(5)));
            Assert.Equal(1, graph.LevelOf(T(40)));
            Assert.Equal(2, graph.LevelOf(T(2)));
            Assert.False(graph.Contains(T(3)));
            Assert.False(graph.Contains(T(41)));
            Assert.False(graph.Contains(T(-1)));
            Assert.Throws<KeyNotFoundException>(() => graph.LevelOf(T(-1)));

            var clone = graph.Clone();
            graph.AddTable(T(3), [], []);
            Assert.False(clone.Contains(T(3)));
            Assert.Equal(2, clone.LevelOf(T(2)));
        }

    }

}
