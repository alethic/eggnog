using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A review stress test of §13 edge 22's last sentence: freezing a context with live children gives each child
    /// subtree its own domain; the subtrees then catch up with the frozen context and run concurrently.
    /// </summary>
    public class FreezeWithLiveChildrenReviewTests
    {

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        [InlineData(14)]
        public void SubtreesOfAContextFrozenUnderThemRunConcurrently(int seed)
        {
            var random = new Random(seed);
            var root = SpikePair.Root(withSpike: false);
            var live = new List<SpikePair> { root };

            // Children two deep under a writable root, which keeps changing after they exist.
            for (var step = 0; step < 200; step++)
            {
                var writable = live.Where(p => p.IsFrozen == false).ToList();
                var pair = writable[random.Next(writable.Count)];
                ContextDifferentialTests.Step(random, pair, live, allowChildren: true, retractions: false);
            }

            root.Freeze();
            var subtrees = root.Children.ToArray();
            var failures = new List<Exception>();
            Parallel.For(0, subtrees.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try
                {
                    var local = new Random(seed * 100 + i);
                    var own = new List<SpikePair>();
                    void Collect(SpikePair p)
                    {
                        own.Add(p);
                        foreach (var c in p.Children)
                            Collect(c);
                    }

                    Collect(subtrees[i]);
                    for (var step = 0; step < 150; step++)
                    {
                        var writable = own.Where(p => p.IsFrozen == false && p.Engine.IsDisposed == false).ToList();
                        if (writable.Count == 0)
                            break;

                        ContextDifferentialTests.Step(local, writable[local.Next(writable.Count)], own, allowChildren: true, retractions: true);
                    }

                    foreach (var p in own.Where(p => p.Engine.IsDisposed == false))
                        p.Check();
                }
                catch (Exception e)
                {
                    lock (failures)
                        failures.Add(e);
                }
            });

            if (failures.Count > 0)
                throw new Xunit.Sdk.XunitException($"{failures.Count} subtree(s) failed; first: {failures[0]}{Environment.NewLine}{root.Program.Trace}");
        }

    }

}
