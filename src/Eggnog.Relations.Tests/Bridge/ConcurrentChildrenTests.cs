using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="ConcurrentChildren"/> and the e-graph's <see cref="EGraph.ParallelismBudget"/> (colored-contexts spec
    /// §10.9, §17 M10.3): the budget caps how many bodies run at once, a single exception comes back unwrapped, children
    /// that would share a thread domain are refused, and two threads on one domain throw. Also the default parallelism of
    /// children (§10.9).
    /// </summary>
    public class ConcurrentChildrenTests
    {

        [Fact]
        public void TheBudgetHasTheGraphsDegreeOfParallelism()
        {
            Assert.Equal(6, new ContextGraph(new ParallelHeuristics(6)).EGraph.ParallelismBudget.Total);
            Assert.Equal(1, new ContextGraph().EGraph.ParallelismBudget.Total);
            Assert.Equal(Math.Max(1, Environment.ProcessorCount), new EGraph().ParallelismBudget.Total);
        }

        /// <summary>
        /// §10.9: a child of a frozen context is serial, a child of a writable context inherits its parent's heuristics (so a
        /// grandchild under a writable child of a frozen root is serial too), and the options override both.
        /// </summary>
        [Fact]
        public void ChildrenTakeTheDefaultParallelism()
        {
            var parallel = new ParallelHeuristics(4);
            var g = new ContextGraph(parallel);
            Assert.Same(parallel, g.Root.Parallelism);
            using (var writable = g.Root.CreateChild())
            using (var grandchild = writable.CreateChild())
            {
                Assert.Same(parallel, writable.Parallelism);
                Assert.Same(parallel, grandchild.Parallelism);
            }

            g.Root.Freeze();
            var wide = new ParallelHeuristics(3);
            using var serial = g.Root.CreateChild();
            using var widened = g.Root.CreateChild(new ContextOptions { Parallelism = wide });
            using var underSerial = serial.CreateChild();
            using var underWide = widened.CreateChild();
            Assert.Same(ParallelHeuristics.Serial, serial.Parallelism);
            Assert.Same(ParallelHeuristics.Serial, underSerial.Parallelism);
            Assert.Same(wide, widened.Parallelism);
            Assert.Same(wide, underWide.Parallelism);

            underWide.Dispose();
            widened.Freeze();
            using var underFrozen = widened.CreateChild();
            Assert.Same(ParallelHeuristics.Serial, underFrozen.Parallelism);
        }

        [Fact]
        public void EveryChildRunsOnceWithItsIndex()
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            g.Root.Freeze();
            var children = Enumerable.Range(0, 10).Select(_ => g.Root.CreateChild()).ToArray();
            var seen = new ConcurrentDictionary<int, EGraphContext>();
            ConcurrentChildren.Run(children, (child, i) => Assert.True(seen.TryAdd(i, child)));
            Assert.Equal(children.Length, seen.Count);
            for (var i = 0; i < children.Length; i++)
                Assert.Same(children[i], seen[i]);

            var count = 0;
            ConcurrentChildren.Run(children, _ => Interlocked.Increment(ref count));
            Assert.Equal(children.Length, count);
            ConcurrentChildren.Run(Array.Empty<EGraphContext>(), _ => throw new InvalidOperationException());
            Assert.Equal(4, g.EGraph.ParallelismBudget.Available);
            foreach (var child in children)
                child.Dispose();
        }

        /// <summary>
        /// The budget caps the bodies running at once: serial children lease one thread each, so at most <c>Total</c> run
        /// together; children with two threads each lease two, so at most <c>Total / 2</c> do. Every thread comes back.
        /// </summary>
        [Theory]
        [InlineData(1, 4)]
        [InlineData(2, 2)]
        [InlineData(4, 1)]
        [InlineData(8, 1)]
        public void TheBudgetCapsTheBodiesRunningAtOnce(int childThreads, int expectedCap)
        {
            const int Total = 4;
            var g = new ContextGraph(new ParallelHeuristics(Total));
            g.Root.Freeze();
            var options = childThreads == 1 ? null : new ContextOptions { Parallelism = new ParallelHeuristics(childThreads) };
            var children = Enumerable.Range(0, 12).Select(_ => g.Root.CreateChild(options)).ToArray();
            var budget = g.EGraph.ParallelismBudget;
            var running = 0;
            var peak = 0;
            var leased = Math.Min(childThreads, Total);

            ConcurrentChildren.Run(children, child =>
            {
                var now = Interlocked.Increment(ref running);
                Assert.InRange(budget.Available, 0, Total - leased);
                Assert.InRange(now * leased, leased, Total);
                InterlockedMax(ref peak, now);
                child.StageRow(g.Mark, [child.FreshId(), g.Unit]);
                child.FlushUpdates();
                Thread.Sleep(5);
                Interlocked.Decrement(ref running);
            });

            Assert.InRange(peak, 1, expectedCap);
            Assert.Equal(Total, budget.Available);
            foreach (var child in children)
            {
                Assert.Equal(1, child.TableSize(g.Mark));
                child.Dispose();
            }
        }

        /// <summary>
        /// A body that throws stops the run and its exception comes back as itself, not wrapped; the budget is whole again
        /// and the other children are usable.
        /// </summary>
        [Fact]
        public void ASingleExceptionIsRethrownUnwrapped()
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            g.Root.Freeze();
            var children = Enumerable.Range(0, 6).Select(_ => g.Root.CreateChild()).ToArray();
            var thrown = new TimeZoneNotFoundException("boom");
            var error = Assert.Throws<TimeZoneNotFoundException>(() => ConcurrentChildren.Run(children, (child, i) =>
            {
                if (i == 3)
                    throw thrown;
            }));
            Assert.Same(thrown, error);
            Assert.Equal(4, g.EGraph.ParallelismBudget.Available);

            // Through the single-child path too.
            Assert.Same(thrown, Assert.Throws<TimeZoneNotFoundException>(() => ConcurrentChildren.Run([children[0]], _ => throw thrown)));

            // A panic in a child's run is that child's (§10.6) and comes back as the PanicException itself.
            var rb = g.EGraph.NewRule("panics", true);
            rb.QueryTable(g.Mark, [rb.NewVar(ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Panic("child panic");
            var rule = rb.Build();
            var panic = Assert.Throws<PanicException>(() => ConcurrentChildren.Run(children, (child, i) =>
            {
                if (i == 1)
                {
                    g.Assert(child, g.Mark, child.FreshId());
                    child.FlushUpdates();
                }

                child.RunRules(rule);
            }));
            Assert.Equal("child panic", panic.PanicMessage);
            Assert.All(children, c => Assert.False(c.IsFaulted));
            Assert.Equal(4, g.EGraph.ParallelismBudget.Available);
            foreach (var child in children)
                child.Dispose();
        }

        /// <summary>
        /// Exceptions from bodies that ran at the same time come back together.
        /// </summary>
        [Fact]
        public void SeveralExceptionsAreAggregated()
        {
            var g = new ContextGraph(new ParallelHeuristics(2));
            g.Root.Freeze();
            var children = Enumerable.Range(0, 2).Select(_ => g.Root.CreateChild()).ToArray();
            using var both = new Barrier(2);
            var error = Assert.Throws<AggregateException>(() => ConcurrentChildren.Run(children, (child, i) =>
            {
                both.SignalAndWait(TimeSpan.FromSeconds(60));
                throw new InvalidTimeZoneException($"body {i}");
            }));
            Assert.Equal(2, error.InnerExceptions.Count);
            Assert.All(error.InnerExceptions, e => Assert.IsType<InvalidTimeZoneException>(e));
            Assert.Equal(2, g.EGraph.ParallelismBudget.Available);
            foreach (var child in children)
                child.Dispose();
        }

        /// <summary>
        /// Children of a writable context share its thread domain (§8.3), so running them concurrently is refused before any
        /// body runs; so are the root, a child together with its own descendant, a child listed twice, a child of another
        /// e-graph, a null child and a disposed one.
        /// </summary>
        [Fact]
        public void ChildrenThatWouldShareADomainAreRefused()
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            var ran = 0;
            void Body(EGraphContext _) => Interlocked.Increment(ref ran);

            using (var a = g.Root.CreateChild())
            using (var b = g.Root.CreateChild())
                Assert.Throws<InvalidOperationException>(() => ConcurrentChildren.Run([a, b], Body));

            Assert.Throws<InvalidOperationException>(() => ConcurrentChildren.Run([g.Root], Body));
            g.Root.Freeze();
            Assert.Throws<InvalidOperationException>(() => ConcurrentChildren.Run([g.Root], Body));

            using var first = g.Root.CreateChild();
            using var second = g.Root.CreateChild();
            Assert.Throws<ArgumentException>(() => ConcurrentChildren.Run([first, second, first], Body));
            Assert.Throws<ArgumentException>(() => ConcurrentChildren.Run([first, null!], Body));
            Assert.Throws<ArgumentNullException>(() => ConcurrentChildren.Run(null!, Body));
            Assert.Throws<ArgumentNullException>(() => ConcurrentChildren.Run([first], (Action<EGraphContext>)null!));

            var other = new ContextGraph(new ParallelHeuristics(4));
            other.Root.Freeze();
            using (var foreign = other.Root.CreateChild())
                Assert.Throws<ArgumentException>(() => ConcurrentChildren.Run([first, foreign], Body));

            first.Freeze();
            using (var below = first.CreateChild())
            {
                Assert.Throws<InvalidOperationException>(() => ConcurrentChildren.Run([first, second, below], Body));
                Assert.Throws<InvalidOperationException>(() => ConcurrentChildren.Run([below, first], Body));
            }

            var gone = g.Root.CreateChild();
            gone.Dispose();
            Assert.Throws<ObjectDisposedException>(() => ConcurrentChildren.Run([second, gone], Body));
            Assert.Equal(0, ran);
            Assert.Equal(4, g.EGraph.ParallelismBudget.Available);
        }

        /// <summary>
        /// Two threads on one thread domain throw (§8.3): while one thread is inside a read of a writable child, a second
        /// thread's write to that child, to its sibling or to their writable parent is refused, not serialized.
        /// </summary>
        [Fact]
        public void TwoThreadsOnOneDomainThrow()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.AddValues([(g.Edge, [a, b, g.Unit])]);
            using var child = g.Root.CreateChild();
            using var sibling = g.Root.CreateChild();
            using var inside = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();

            var holder = Task.Factory.StartNew(() => child.ForEach(g.Edge, _ =>
            {
                inside.Set();
                done.Wait(TimeSpan.FromSeconds(60));
            }), TaskCreationOptions.LongRunning);

            try
            {
                Assert.True(inside.Wait(TimeSpan.FromSeconds(60)));
                Assert.Throws<InvalidOperationException>(() => child.FlushUpdates());
                Assert.Throws<InvalidOperationException>(() => child.TableSize(g.Edge));
                Assert.Throws<InvalidOperationException>(() => sibling.RunRules(g.EdgeToPath()));
                Assert.Throws<InvalidOperationException>(() => g.Root.FlushUpdates());
                Assert.Throws<InvalidOperationException>(() => g.Root.CreateChild());
            }
            finally
            {
                done.Set();
                holder.Wait(TimeSpan.FromSeconds(60));
            }

            Assert.True(holder.IsCompletedSuccessfully);
            Assert.False(child.IsFaulted);
            Assert.False(sibling.IsFaulted);
            Assert.Equal(1, child.TableSize(g.Edge));
        }

        /// <summary>
        /// Two bodies of one run never share a domain: children of a frozen root each get their own, so their writes and
        /// runs go side by side without a refusal.
        /// </summary>
        [Fact]
        public void ChildrenOfAFrozenContextHaveDomainsOfTheirOwn()
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            var rule = g.EdgeToPath();
            g.Root.Freeze();
            var children = Enumerable.Range(0, 8).Select(_ => g.Root.CreateChild()).ToArray();
            Assert.Equal(children.Length, children.Select(c => c.Context.Domain).Distinct().Count());
            ConcurrentChildren.Run(children, child =>
            {
                var x = child.FreshId();
                var y = child.FreshId();
                g.Assert(child, g.Edge, x, y);
                child.FlushUpdates();
                child.RunRules(rule);
                Assert.Equal(1, child.TableSize(g.Path));
            });

            foreach (var child in children)
                child.Dispose();
        }

        /// <summary>
        /// A body may run grandchildren concurrently in turn; the nested run uses the lease its thread holds, so it cannot
        /// wait forever on a budget the outer run has leased in full.
        /// </summary>
        [Fact]
        public void NestedRunsUseTheLeaseTheyHold()
        {
            const int Total = 2;
            var g = new ContextGraph(new ParallelHeuristics(Total));
            g.Root.Freeze();
            var children = Enumerable.Range(0, Total).Select(_ => g.Root.CreateChild()).ToArray();
            foreach (var child in children)
                child.Freeze();

            var grandchildren = children.Select(c => Enumerable.Range(0, 3).Select(_ => c.CreateChild()).ToArray()).ToArray();
            var ran = 0;
            var run = Task.Run(() => ConcurrentChildren.Run(children, (child, i) =>
            {
                ConcurrentChildren.Run(grandchildren[i], grandchild =>
                {
                    Assert.InRange(g.EGraph.ParallelismBudget.Available, 0, Total - 1);
                    grandchild.StageRow(g.Mark, [grandchild.FreshId(), g.Unit]);
                    grandchild.FlushUpdates();
                    Interlocked.Increment(ref ran);
                });
            }));

            Assert.True(run.Wait(TimeSpan.FromSeconds(120)));
            Assert.Equal(6, ran);
            Assert.Equal(Total, g.EGraph.ParallelismBudget.Available);
            foreach (var line in grandchildren)
                foreach (var grandchild in line)
                    grandchild.Dispose();

            foreach (var child in children)
                child.Dispose();
        }

        static void InterlockedMax(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var seen = Interlocked.CompareExchange(ref target, value, current);
                if (seen == current)
                    return;

                current = seen;
            }
        }

    }

}
