using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="DependencyGraph"/> (egglog's core-relations/src/dependency_graph.rs).
    /// </summary>
    public class DependencyGraphTests
    {

        static TableId T(int i) => new(i);

        [Fact]
        public void LevelIsOneAboveTheHighestReadDependency()
        {
            var graph = new DependencyGraph();
            graph.AddTable(T(0), [], []);
            graph.AddTable(T(1), [], [T(0)]);
            graph.AddTable(T(2), [T(0)], []);
            graph.AddTable(T(3), [T(2), T(0)], [T(1)]);
            graph.AddTable(T(4), [T(1)], []);

            Assert.Equal(0, graph.LevelOf(T(0)));
            Assert.Equal(0, graph.LevelOf(T(1)));
            Assert.Equal(1, graph.LevelOf(T(2)));
            Assert.Equal(2, graph.LevelOf(T(3)));
            Assert.Equal(1, graph.LevelOf(T(4)));

            // Strata are contiguous and keep insertion order.
            Assert.Equal(3, graph.StratumCount);
            Assert.Equal(new[] { T(0), T(1) }, graph.Stratum(0).ToArray());
            Assert.Equal(new[] { T(2), T(4) }, graph.Stratum(1).ToArray());
            Assert.Equal(new[] { T(3) }, graph.Stratum(2).ToArray());
            Assert.True(graph.Contains(T(4)));
            Assert.False(graph.Contains(T(5)));
        }

        [Fact]
        public void WriteDependenciesAreDeduplicatedInOrderAndMayBeUnknown()
        {
            var graph = new DependencyGraph();
            graph.AddTable(T(0), [], [T(7), T(3), T(7), T(0)]);

            Assert.Equal(new[] { T(7), T(3), T(0) }, graph.WriteDependencies(T(0)).ToArray());
            Assert.True(graph.WriteDependencies(T(9)).IsEmpty);
        }

        [Fact]
        public void InvalidRegistrationsThrowAndChangeNothing()
        {
            var graph = new DependencyGraph();
            graph.AddTable(T(0), [], []);

            Assert.Throws<InvalidOperationException>(() => graph.AddTable(T(0), [], []));
            Assert.Throws<ArgumentException>(() => graph.AddTable(T(1), [T(5)], []));
            Assert.Throws<ArgumentException>(() => graph.AddTable(T(1), [T(1)], []));
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => graph.LevelOf(T(1)));

            Assert.False(graph.Contains(T(1)));
            Assert.Equal(1, graph.StratumCount);
            graph.AddTable(T(1), [T(0)], []);
            Assert.Equal(1, graph.LevelOf(T(1)));
        }

        [Fact]
        public void ClonesAreIndependent()
        {
            var graph = new DependencyGraph();
            graph.AddTable(T(0), [], [T(1)]);
            var clone = graph.Clone();
            graph.AddTable(T(1), [T(0)], []);

            Assert.False(clone.Contains(T(1)));
            Assert.Equal(1, clone.StratumCount);
            Assert.Equal(2, graph.StratumCount);
            Assert.Equal(new[] { T(1) }, clone.WriteDependencies(T(0)).ToArray());
            clone.AddTable(T(1), [], []);
            Assert.Equal(0, clone.LevelOf(T(1)));
            Assert.Equal(1, graph.LevelOf(T(1)));
        }

    }

}
