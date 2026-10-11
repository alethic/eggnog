using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="FunDeps"/>: egglog's functional-dependency closure (core-relations/src/query.rs:952-1002),
    /// against a naive reference.
    /// </summary>
    public class FunDepsTests
    {

        static Variable V(int i) => new(i);

        static int[] Members(FixedBitSet set) => Enumerable.Range(0, set.Length).Where(set.Contains).ToArray();

        [Fact]
        public void ClosureRunsToAFixpoint()
        {
            var fd = new FunDeps();
            fd.AddDependency([V(2)], [V(3)]);
            fd.AddDependency([V(0), V(1)], [V(2)]);
            fd.AddDependency([V(3)], [V(4), V(5)]);
            Assert.Equal([0, 1, 2, 3, 4, 5], Members(fd.Closure([V(0), V(1)], 6)));
            Assert.Equal([0], Members(fd.Closure([V(0)], 6)));
            Assert.Equal([3, 4, 5], Members(fd.Closure([V(3)], 6)));
        }

        [Fact]
        public void EmptyAntecedentIsDropped()
        {
            var fd = new FunDeps();
            fd.AddDependency([], [V(1)]);
            Assert.Equal(0, fd.Count);
            Assert.Equal([0], Members(fd.Closure([V(0)], 2)));
        }

        [Fact]
        public void ClosureGrowsToTheDependencyVariables()
        {
            var fd = new FunDeps();
            fd.AddDependency([V(0)], [V(9)]);
            Assert.Equal(10, fd.VariableBound);
            var closure = fd.Closure([V(0)], 1);
            Assert.True(closure.Length >= 10);
            Assert.Equal([0, 9], Members(closure));
        }

        [Fact]
        public void WildcardsAreRejected()
        {
            var fd = new FunDeps();
            Assert.Throws<ArgumentException>(() => fd.AddDependency([Variable.Placeholder], [V(0)]));
        }

        [Fact]
        public void ClosureMatchesNaiveReference()
        {
            var rng = new Random(7);
            for (var iteration = 0; iteration < 500; iteration++)
            {
                var fd = new FunDeps();
                var deps = new List<(int[] A, int[] C)>();
                for (var d = rng.Next(0, 6); d > 0; d--)
                {
                    var a = Enumerable.Range(0, rng.Next(0, 3)).Select(_ => rng.Next(8)).ToArray();
                    var c = Enumerable.Range(0, rng.Next(0, 3)).Select(_ => rng.Next(8)).ToArray();
                    fd.AddDependency(a.Select(V).ToArray(), c.Select(V).ToArray());
                    if (a.Length > 0)
                        deps.Add((a, c));
                }

                var seed = Enumerable.Range(0, rng.Next(0, 4)).Select(_ => rng.Next(8)).ToArray();
                var expected = new HashSet<int>(seed);
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var (a, c) in deps)
                        if (a.All(expected.Contains))
                            foreach (var x in c)
                                changed |= expected.Add(x);
                }

                Assert.Equal(expected.Order(), Members(fd.Closure(seed.Select(V).ToArray(), 8)));
            }
        }

    }

}
