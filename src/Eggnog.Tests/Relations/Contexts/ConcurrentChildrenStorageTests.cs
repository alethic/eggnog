using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Children of a frozen root driven concurrently (colored-contexts spec §17 M9.4 gate, I11, I24), ported from the
    /// spike's <c>ChildrenOfAFrozenRootAgreeWhenDrivenConcurrently</c>: 16 children, each with its own thread domain, run
    /// random programs on 8 threads (grandchildren and freezes included), and each agrees with the reference afterwards;
    /// the frozen root is untouched.
    /// </summary>
    public class ConcurrentChildrenStorageTests
    {

        /// <summary>
        /// Sixteen children of a frozen root, driven by random programs on eight threads, each agree with the
        /// reference; the root is unchanged.
        /// </summary>
        [Fact]
        public void ChildrenOfAFrozenRootAgreeWhenDrivenConcurrently()
        {
            var root = SpikePair.Root(withSpike: false);
            var setup = new Random(7);
            var scratch = new List<SpikePair> { root };
            for (var step = 0; step < 120; step++)
                ContextDifferentialTests.Step(setup, root, scratch, allowChildren: false, retractions: true);

            root.Freeze();
            root.Check();
            var rootRows = root.Program.Schema.Tables.Select(t => root.Facade.Rows(t).Count).ToArray();
            var rootUnions = root.Engine.DisplacedCount;

            var children = Enumerable.Range(0, 16).Select(_ => root.CreateChild()).ToArray();
            var lives = new List<SpikePair>[children.Length];
            try
            {
                Parallel.For(0, children.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                {
                    var random = new Random(1000 + i);
                    var own = new List<SpikePair> { children[i] };
                    lives[i] = own;
                    for (var step = 0; step < 150; step++)
                    {
                        var writable = own.Where(p => p.IsFrozen == false).ToList();
                        if (writable.Count == 0)
                            break;

                        ContextDifferentialTests.Step(random, writable[random.Next(writable.Count)], own, allowChildren: true, retractions: true);
                    }
                });

                foreach (var own in lives)
                    foreach (var pair in own)
                        pair.Check();
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"{e.Message}{Environment.NewLine}Program:{Environment.NewLine}{root.Program.Trace}", e);
            }

            // The children wrote nothing of the root's.
            Assert.Equal(rootRows, root.Program.Schema.Tables.Select(t => root.Facade.Rows(t).Count).ToArray());
            Assert.Equal(rootUnions, root.Engine.DisplacedCount);
            root.Engine.CheckInvariants();
        }

    }

}
