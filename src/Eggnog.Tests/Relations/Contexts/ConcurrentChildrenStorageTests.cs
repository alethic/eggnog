using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Contexts driven concurrently, each by a task of its own as a host drives them. Children of a frozen root: 16
    /// children, each with its own thread domain, run random programs 8 at a time (grandchildren and freezes included),
    /// and each agrees with the reference afterwards; the frozen root is untouched. Grandchildren of frozen children,
    /// created, driven and disposed concurrently on their shared frozen parents. And the subtrees of a context frozen
    /// under them, which get thread domains of their own. <see cref="Soak"/> runs all three over fresh seeds, child
    /// counts and numbers of tasks at a time.
    /// </summary>
    public class ConcurrentChildrenStorageTests
    {

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public ConcurrentChildrenStorageTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Sixteen children of a frozen root, driven by random programs on tasks of their own, eight at a time, each
        /// agree with the reference; the root is unchanged.
        /// </summary>
        [Fact]
        public void ChildrenOfAFrozenRootAgreeWhenDrivenConcurrently()
        {
            ChildrenOfAFrozenRoot(7, 1000, 16, 8);
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
            GrandchildrenOfFrozenChildren(seed, 4, 32, 8);
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
            SubtreesOfAContextFrozenUnderThem(seed, 200, 8);
        }

        /// <summary>
        /// The soak (<see cref="ConcurrentSoak"/>, <c>EGGNOG_CONCURRENT_SOAK</c>): each seed picks one of the three
        /// scenarios and draws its shape: 2 to 32 children of a frozen root; or 1 to 8 frozen children under 2 to 64
        /// concurrent tasks of grandchildren; or 40 to 400 steps of a writable tree before its root freezes under its
        /// subtrees. Each runs 2 to 8 tasks at a time.
        /// </summary>
        [Fact]
        public void Soak()
        {
            ConcurrentSoak.Run(output, nameof(ConcurrentChildrenStorageTests), seed =>
            {
                var random = new Random(seed);
                var scenario = random.Next(3);
                var threads = random.Next(2, 9);
                switch (scenario)
                {
                    case 0:
                        var children = random.Next(2, 33);
                        return ($"{children} children of a frozen root, {threads} at a time", () => ChildrenOfAFrozenRoot(seed, unchecked(seed * 1000), children, threads));
                    case 1:
                        var parents = random.Next(1, 9);
                        var tasks = random.Next(2, 65);
                        return ($"{tasks} tasks of grandchildren under {parents} frozen children, {threads} at a time", () => GrandchildrenOfFrozenChildren(seed, parents, tasks, threads));
                    default:
                        var steps = random.Next(40, 401);
                        return ($"subtrees of a root frozen after {steps} steps, {threads} at a time", () => SubtreesOfAContextFrozenUnderThem(seed, steps, threads));
                }
            });
        }

        /// <summary>
        /// Drives <paramref name="children"/> children of a frozen root concurrently, each on a task of its own by its
        /// own random program, at most <paramref name="threads"/> at a time, and checks every context they created
        /// against the reference and the root against its state before.
        /// </summary>
        /// <param name="setupSeed">The seed of the root's program.</param>
        /// <param name="childSeedBase">The seed of the first child's program; the others follow.</param>
        /// <param name="children">The number of children.</param>
        /// <param name="threads">The most tasks that drive them at once.</param>
        static void ChildrenOfAFrozenRoot(int setupSeed, int childSeedBase, int children, int threads)
        {
            var root = ContextPair.Root();
            var setup = new Random(setupSeed);
            var scratch = new List<ContextPair> { root };
            for (var step = 0; step < 120; step++)
                ContextDifferentialTests.Step(setup, root, scratch, allowChildren: false, retractions: true);

            root.Freeze();
            root.Check();
            var rootRows = root.Program.Schema.Tables.Select(t => root.Facade.Rows(t).Count).ToArray();
            var rootUnions = root.Engine.DisplacedCount;

            var pairs = Enumerable.Range(0, children).Select(_ => root.CreateChild()).ToArray();

            // Children of a frozen root, none an ancestor of another: each has a thread domain of its own.
            Assert.Equal(children, pairs.Select(p => p.Engine.Domain).Distinct().Count());
            var lives = new List<ContextPair>[pairs.Length];
            try
            {
                var errors = HostTasks.Run(pairs.Length, threads, i =>
                {
                    var random = new Random(unchecked(childSeedBase + i));
                    var own = new List<ContextPair> { pairs[i] };
                    lives[i] = own;
                    for (var step = 0; step < 150; step++)
                    {
                        var writable = own.Where(p => p.IsFrozen == false).ToList();
                        if (writable.Count == 0)
                            break;

                        ContextDifferentialTests.Step(random, writable[random.Next(writable.Count)], own, allowChildren: true, retractions: true);
                    }
                });

                if (errors.Length > 0)
                    throw new AggregateException(errors);

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
        /// Freezes <paramref name="parents"/> children of a frozen root, each after a random program of its own; then
        /// <paramref name="tasks"/> tasks, at most <paramref name="threads"/> at a time, each create, drive, check and
        /// dispose three grandchildren in turn under one of them, and every parent is checked afterwards.
        /// </summary>
        /// <param name="seed">The seed of every program.</param>
        /// <param name="parents">The number of frozen children.</param>
        /// <param name="tasks">The number of tasks.</param>
        /// <param name="threads">The most tasks that run at once.</param>
        static void GrandchildrenOfFrozenChildren(int seed, int parents, int tasks, int threads)
        {
            var root = ContextPair.Root();
            var setup = new Random(seed);
            var scratch = new List<ContextPair> { root };
            for (var step = 0; step < 120; step++)
                ContextDifferentialTests.Step(setup, root, scratch, allowChildren: false, retractions: false);

            root.Freeze();
            var children = Enumerable.Range(0, parents).Select(_ => root.CreateChild()).ToArray();
            foreach (var child in children)
            {
                var own = new List<ContextPair> { child };
                for (var step = 0; step < 60; step++)
                    ContextDifferentialTests.Step(setup, child, own, allowChildren: false, retractions: true);

                child.Freeze();
                child.Check();
            }

            var failures = HostTasks.Run(tasks, threads, i =>
            {
                var random = new Random(unchecked(seed * 1000 + i));
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
            });

            if (failures.Length > 0)
                throw new Xunit.Sdk.XunitException($"{failures.Length} task(s) failed; first: {failures[0]}{Environment.NewLine}{root.Program.Trace}");

            foreach (var child in children)
                child.Check();
        }

        /// <summary>
        /// Builds a tree up to two deep under a writable root, which keeps changing after its children exist, by
        /// <paramref name="steps"/> random steps; freezes the root; then drives each child subtree by its own program on
        /// a task of its own, at most <paramref name="threads"/> at a time, and checks every live context of it against
        /// the reference.
        /// </summary>
        /// <param name="seed">The seed of every program.</param>
        /// <param name="steps">The number of steps before the root freezes.</param>
        /// <param name="threads">The most tasks that drive the subtrees at once.</param>
        static void SubtreesOfAContextFrozenUnderThem(int seed, int steps, int threads)
        {
            var random = new Random(seed);
            var root = ContextPair.Root();
            var live = new List<ContextPair> { root };

            // Children two deep under a writable root, which keeps changing after they exist.
            for (var step = 0; step < steps; step++)
            {
                var writable = live.Where(p => p.IsFrozen == false).ToList();
                var pair = writable[random.Next(writable.Count)];
                ContextDifferentialTests.Step(random, pair, live, allowChildren: true, retractions: false);
            }

            root.Freeze();
            var subtrees = root.Children.ToArray();
            var failures = HostTasks.Run(subtrees.Length, threads, i =>
            {
                var local = new Random(unchecked(seed * 100 + i));
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
            });

            if (failures.Length > 0)
                throw new Xunit.Sdk.XunitException($"{failures.Length} subtree(s) failed; first: {failures[0]}{Environment.NewLine}{root.Program.Trace}");
        }

    }

}
