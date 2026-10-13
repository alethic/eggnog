using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Contexts driven concurrently. Children of a frozen root: 16 children, each with its own thread domain, run
    /// random programs on 8 threads (grandchildren and freezes included), and each agrees with the reference
    /// afterwards; the frozen root is untouched. Grandchildren of frozen children, created, driven and disposed
    /// concurrently on their shared frozen parents. And the subtrees of a context frozen under them, which get thread
    /// domains of their own.
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
            var root = ContextPair.Root();
            var setup = new Random(7);
            var scratch = new List<ContextPair> { root };
            for (var step = 0; step < 120; step++)
                ContextDifferentialTests.Step(setup, root, scratch, allowChildren: false, retractions: true);

            root.Freeze();
            root.Check();
            var rootRows = root.Program.Schema.Tables.Select(t => root.Facade.Rows(t).Count).ToArray();
            var rootUnions = root.Engine.DisplacedCount;

            var children = Enumerable.Range(0, 16).Select(_ => root.CreateChild()).ToArray();
            var lives = new List<ContextPair>[children.Length];
            try
            {
                Parallel.For(0, children.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                {
                    var random = new Random(1000 + i);
                    var own = new List<ContextPair> { children[i] };
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

        /// <summary>
        /// Grandchildren of frozen children run on different threads at once, and are created and disposed concurrently
        /// on their shared frozen parents, while other tasks read those parents. Every grandchild agrees with the
        /// reference.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void GrandchildrenOfFrozenChildrenAgreeWhenDrivenConcurrently(int seed)
        {
            var root = ContextPair.Root();
            var setup = new Random(seed);
            var scratch = new List<ContextPair> { root };
            for (var step = 0; step < 120; step++)
                ContextDifferentialTests.Step(setup, root, scratch, allowChildren: false, retractions: false);

            root.Freeze();
            var children = Enumerable.Range(0, 4).Select(_ => root.CreateChild()).ToArray();
            foreach (var child in children)
            {
                var own = new List<ContextPair> { child };
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
                        var own = new List<ContextPair> { grandchild };
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

        /// <summary>
        /// Freezing a context with live children gives each child subtree its own thread domain; the subtrees then
        /// catch up with the frozen context and run concurrently, and every context of them agrees with the reference.
        /// </summary>
        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        [InlineData(14)]
        public void SubtreesOfAContextFrozenUnderThemRunConcurrently(int seed)
        {
            var random = new Random(seed);
            var root = ContextPair.Root();
            var live = new List<ContextPair> { root };

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
                    var own = new List<ContextPair>();
                    void Collect(ContextPair p)
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
