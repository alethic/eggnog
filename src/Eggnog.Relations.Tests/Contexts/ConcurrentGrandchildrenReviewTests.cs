using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A review stress test beyond <see cref="ConcurrentChildrenStorageTests"/>: grandchildren of frozen children run on
    /// different threads at once, and are created and disposed concurrently on their shared frozen parents, while other
    /// tasks read those parents. Every grandchild must agree with the reference.
    /// </summary>
    public class ConcurrentGrandchildrenReviewTests
    {

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void GrandchildrenOfFrozenChildrenAgreeWhenDrivenConcurrently(int seed)
        {
            var root = SpikePair.Root(withSpike: false);
            var setup = new Random(seed);
            var scratch = new List<SpikePair> { root };
            for (var step = 0; step < 120; step++)
                ContextDifferentialTests.Step(setup, root, scratch, allowChildren: false, retractions: false);

            root.Freeze();
            var children = Enumerable.Range(0, 4).Select(_ => root.CreateChild()).ToArray();
            foreach (var child in children)
            {
                var own = new List<SpikePair> { child };
                for (var step = 0; step < 60; step++)
                    ContextDifferentialTests.Step(setup, child, own, allowChildren: false, retractions: true);

                child.Freeze();
                child.Check();
            }

            var failures = new List<Exception>();
            Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try
                {
                    var random = new Random(seed * 1000 + i);
                    var parent = children[i % children.Length];
                    for (var round = 0; round < 3; round++)
                    {
                        var grandchild = parent.CreateChild();
                        var own = new List<SpikePair> { grandchild };
                        for (var step = 0; step < 80; step++)
                        {
                            var writable = own.Where(p => p.IsFrozen == false).ToList();
                            if (writable.Count == 0)
                                break;

                            ContextDifferentialTests.Step(random, writable[random.Next(writable.Count)], own, allowChildren: true, retractions: true);
                        }

                        foreach (var pair in own)
                            pair.Check();

                        // Dispose leaves first.
                        foreach (var pair in own.OrderByDescending(p => p.Depth))
                            if (pair.Engine.IsDisposed == false)
                                pair.Dispose();
                    }
                }
                catch (Exception e)
                {
                    lock (failures)
                        failures.Add(e);
                }
            });

            if (failures.Count > 0)
                throw new Xunit.Sdk.XunitException($"{failures.Count} task(s) failed; first: {failures[0]}{Environment.NewLine}{root.Program.Trace}");

            foreach (var child in children)
                child.Check();
        }

    }

}
