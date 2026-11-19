using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Children of a frozen context running concurrently, each on a task of its own as a host drives them: they take the
    /// default parallelism, have thread domains of their own, keep a panic in their run to themselves, and may run
    /// grandchildren concurrently in turn; with the default heuristics they do all their work on their own task's thread,
    /// so siblings never fan work out to the thread pool. Two threads on one domain throw.
    /// </summary>
    /// <remarks>
    /// The class runs in <see cref="ThreadPoolProbeCollection"/>: its tasks must meet at barriers within a bounded wait,
    /// and the growth bound raises the pool's minimum and counts the work items the whole pool completes while it runs,
    /// which only a test that runs alone may do.
    /// </remarks>
    [Collection(ThreadPoolProbeCollection.Name)]
    public class ConcurrentChildrenTests
    {

        /// <summary>
        /// How long tasks wait for each other at a barrier, and the test for the thread pool to go idle.
        /// </summary>
        static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

        /// <summary>
        /// A child of a frozen context is serial, a child of a writable context inherits its parent's heuristics (so a
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

        /// <summary>
        /// A panic in one child's run is that child's alone: its task fails with the <see cref="PanicException"/> itself,
        /// while its siblings, running the same rule on tasks of their own, finish; no child is faulted.
        /// </summary>
        [Fact]
        public void APanicInOneChildsRunIsThatChildsAlone()
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            g.Root.Freeze();
            var children = Enumerable.Range(0, 6).Select(_ => g.Root.CreateChild()).ToArray();
            var rb = g.EGraph.NewRule("panics", true);
            rb.QueryTable(g.Mark, [rb.NewVar(ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Panic("child panic");
            var rule = rb.Build();

            var tasks = children.Select((child, i) => Task.Run(() =>
            {
                if (i == 1)
                {
                    g.Assert(child, g.Mark, child.FreshId());
                    child.FlushUpdates();
                }

                child.RunRules(rule);
            })).ToArray();

            var error = Assert.Throws<AggregateException>(() => Task.WaitAll(tasks));
            var panic = Assert.IsType<PanicException>(Assert.Single(error.InnerExceptions));
            Assert.Equal("child panic", panic.PanicMessage);
            Assert.True(tasks[1].IsFaulted);
            Assert.All(tasks.Where((_, i) => i != 1), task => Assert.True(task.IsCompletedSuccessfully));
            Assert.All(children, c => Assert.False(c.IsFaulted));
            foreach (var child in children)
                child.Dispose();
        }

        /// <summary>
        /// Two threads on one thread domain throw: while one thread is inside a read of a writable child, a second
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
        /// Children of a frozen root each get a thread domain of their own, so their writes and runs, each on a task of
        /// its own and all at once (the tasks meet at a barrier first), go side by side without a refusal.
        /// </summary>
        [Fact]
        public void ChildrenOfAFrozenContextHaveDomainsOfTheirOwn()
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            var rule = g.EdgeToPath();
            g.Root.Freeze();
            var children = Enumerable.Range(0, 8).Select(_ => g.Root.CreateChild()).ToArray();
            Assert.Equal(children.Length, children.Select(c => c.Context.Domain).Distinct().Count());
            using var start = new Barrier(children.Length);
            Task.WaitAll(children.Select(child => Task.Run(() =>
            {
                Assert.True(start.SignalAndWait(Patience));
                var x = child.FreshId();
                var y = child.FreshId();
                g.Assert(child, g.Edge, x, y);
                child.FlushUpdates();
                child.RunRules(rule);
                Assert.Equal(1, child.TableSize(g.Path));
            })).ToArray());

            foreach (var child in children)
                child.Dispose();
        }

        /// <summary>
        /// A child's task may in turn run grandchildren concurrently, each on a task of its own, under the child once it
        /// is frozen: every grandchild's write lands, and nothing waits forever.
        /// </summary>
        [Fact]
        public void ATaskMayRunGrandchildrenConcurrentlyInTurn()
        {
            var g = new ContextGraph(new ParallelHeuristics(2));
            g.Root.Freeze();
            var children = Enumerable.Range(0, 2).Select(_ => g.Root.CreateChild()).ToArray();
            foreach (var child in children)
                child.Freeze();

            var grandchildren = children.Select(c => Enumerable.Range(0, 3).Select(_ => c.CreateChild()).ToArray()).ToArray();
            var ran = 0;
            var run = Task.WhenAll(grandchildren.Select(line => Task.Run(() => Task.WaitAll(line.Select(grandchild => Task.Run(() =>
            {
                grandchild.StageRow(g.Mark, [grandchild.FreshId(), g.Unit]);
                grandchild.FlushUpdates();
                Interlocked.Increment(ref ran);
            })).ToArray()))));

            Assert.True(run.Wait(TimeSpan.FromSeconds(120)));
            Assert.Equal(6, ran);
            foreach (var line in grandchildren)
                foreach (var grandchild in line)
                {
                    Assert.Equal(1, grandchild.TableSize(g.Mark));
                    grandchild.Dispose();
                }

            foreach (var child in children)
                child.Dispose();
        }

        const int Siblings = 8;

        /// <summary>
        /// How many of the siblings have their rule sets forced onto the parallel executor; the others run theirs on
        /// the serial executor, where the default heuristics put them.
        /// </summary>
        const int Forced = 6;

        const int Sources = 110;
        const int Targets = 100;

        /// <summary>
        /// The pool's minimum while the siblings run: well above their tasks plus the three workers each could queue at
        /// four threads.
        /// </summary>
        const int MinPoolThreads = 64;

        /// <summary>
        /// How long the thread pool must complete and queue no work item before the siblings start: longer than the
        /// work a test host gives the pool around a test's start takes to finish. A dotnet test session reports each
        /// test over a pipe, and its log writes leave timers that fire a second later.
        /// </summary>
        static readonly TimeSpan IdlePool = TimeSpan.FromSeconds(1.5);

        /// <summary>
        /// How long the thread pool must complete and queue no work item after the siblings ran before their count is
        /// read: a work item a pool thread ran counts only once that thread is back in the pool, which can be after the
        /// task waiting for it has resumed.
        /// </summary>
        static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// How many times the siblings run at most: the pool's count must keep within the bound in one of the runs.
        /// </summary>
        const int Runs = 3;

        /// <summary>
        /// The thread-pool growth bound: siblings of a frozen root with the default heuristics, each driven by a task of
        /// its own, do all their work on their own task's thread, so <c>N</c> siblings use <c>N</c> threads and fan
        /// nothing out to the thread pool. The root has four threads, on which a sibling that took the root's heuristics
        /// would fan its work out. Six of the eight siblings have their rule sets forced onto the parallel executor
        /// (<see cref="Database.ParallelRuleSetsOverride"/>), and the other two run theirs on the serial executor,
        /// where the default heuristics put them; each holds more rows of its own than
        /// <see cref="ParallelHeuristics.DbLevelThreshold"/>, above which a database with threads goes parallel
        /// unforced, so the work is as large as work the root's heuristics fan out. The siblings meet at a barrier and
        /// then run side by side, each saturating the same rules over 11,000 edges of its own: a rule that calls the
        /// probe primitive on every match, and a lattice that the probe's minimum merges, in the merges after the rules
        /// and in the rebuild after a union (<see cref="ChildThreadProbe"/>). While they run, the pool's minimum is
        /// raised above what they could queue, so that a work item a sibling queued can start on a pool thread without
        /// the pool's injection delay and show as a stray, and the work items the pool completes are counted: the
        /// siblings' own tasks, and nothing more. The control, a sibling given four threads through
        /// <see cref="ContextOptions.Parallelism"/>, shows that the probe sees fan-out: its two rules meet at a
        /// barrier, so one of them runs on another thread.
        /// </summary>
        /// <remarks>
        /// What it proves: while the siblings run at the same time, every call the engine makes into user code for a
        /// default sibling (its rules' actions and primitives, on the serial and the parallel executor alike, and its
        /// merges) arrives on the sibling's task thread, and the siblings queue no work item on the thread pool: the
        /// pool completes no work item but their eight tasks (a task that <see cref="Task.WaitAll(Task[])"/> runs
        /// inline on the waiting thread counts as one too). The two checks see fan-out differently. A stray names the
        /// sibling whose call left its thread, but only when a queued worker starts before the sibling waiting for it
        /// has run every item itself, which takes free pool threads: without the raised minimum the siblings would hold
        /// every thread a small machine's pool has, which grows too slowly to start a worker in time, and on a busy
        /// machine a worker can still start too late. The count sees the work items a sibling queued, whichever thread
        /// ran them, and so also fan-out of work that calls no user code (index builds, compaction, a rebuild's table
        /// operations). A work item a pool thread ran counts only once that thread is back in the pool, which can be
        /// after the task waiting for it has resumed, so the count is read once it covers the siblings' tasks and the
        /// pool has then been idle for <see cref="Settle"/>; on a busy machine an item whose thread is kept from the
        /// pool for longer than that can still be missed. It counts the whole process's pool, so the siblings start
        /// only once the pool has completed and queued nothing for <see cref="IdlePool"/>, by when the work a test host
        /// gave the pool around the test's start has run. A host's timers can still complete work items while the
        /// siblings run (a dotnet test session's telemetry completes one every ten seconds, and about ten at once when
        /// it uploads), so a count over the bound is taken again with fresh siblings, up to <see cref="Runs"/> runs in
        /// all: siblings that queue work queue it in every run, while a host's timer fires in few of them. A child's
        /// merges, the rebuild's included, are serial whatever its heuristics, so the merge function's calls cannot
        /// stray today; the probe counts them so that a merge given a parallel arm would show. What it does not prove:
        /// that work which calls no user code (index builds, compaction, a rebuild's table operations) stays off a
        /// thread of its own (a long-running task, a dedicated thread), which is no pool work item; anything about
        /// siblings given threads of their own, which have no global bound; or how many siblings a host should run at
        /// once, which is the host's choice.
        /// </remarks>
        [Fact]
        public void SiblingsWithTheDefaultHeuristicsRunOnTheirOwnThreadsOnly()
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            var probe = new ChildThreadProbe(g);
            var least = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(probe.Min, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "least",
            });

            var rb = g.EGraph.NewRule("probe", true);
            rb.QueryTable(g.Edge, [rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.CallExternalFunc(probe.Primitive, [], g.UnitType, static () => "probe");
            var probeRule = rb.Build();

            rb = g.EGraph.NewRule("least", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Edge, [x, rb.NewVar(ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Set(least, [x, RuleEntry.Const(g.Int(0), g.I64)]);
            var leastRule = rb.Build();

            var rules = new[] { g.EdgeToPath(), g.Transitive(), probeRule, leastRule };
            var sources = Enumerable.Range(0, Sources).Select(_ => g.Root.FreshId()).ToArray();
            var targets = Enumerable.Range(0, Targets).Select(_ => g.Root.FreshId()).ToArray();
            g.Root.Freeze();

            // Room for every worker the siblings could queue at the root's four threads, started without the pool's
            // injection delay; restored before the control.
            ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
            Assert.SkipUnless(ThreadPool.SetMinThreads(Math.Max(minWorkers, MinPoolThreads), minIo), $"The thread pool refuses a minimum of {MinPoolThreads} worker threads (a forced minimum or maximum, or the Windows thread pool).");
            EGraphContext[] siblings;
            long workItems;
            try
            {
                (siblings, workItems) = RunSiblings();
                for (var run = 1; workItems > Siblings && run < Runs; run++)
                {
                    foreach (var sibling in siblings)
                        sibling.Dispose();

                    (siblings, workItems) = RunSiblings();
                }
            }
            finally
            {
                ThreadPool.SetMinThreads(minWorkers, minIo);
            }

            // Each sibling's task is one work item, whether a pool thread ran it or Task.WaitAll ran it inline.
            Assert.True(workItems <= Siblings, $"the thread pool completed more than {Siblings} work items in each of {Runs} runs of the siblings, {workItems} in the last");

            // The control: a sibling given four threads runs two rules on two threads at once, which the probe sees.
            using var meet = new Barrier(2);
            var meetings = new ConcurrentQueue<bool>();
            var meeting = BridgeTestUtil.Register(g.EGraph, (state, _) =>
            {
                meetings.Enqueue(meet.SignalAndWait(TimeSpan.FromSeconds(10)));
                probe.Observe(state);
                return g.Unit.Rep;
            });

            var meetRules = Enumerable.Range(0, 2).Select(r =>
            {
                var b = g.EGraph.NewRule("meet-" + r, true);
                b.QueryTable(g.Edge, [b.NewVar(ColumnTy.Id), b.NewVar(ColumnTy.Id), b.NewVar(g.UnitType)], false);
                b.CallExternalFunc(meeting, [], g.UnitType, static () => "meet");
                return b.Build();
            }).ToArray();

            using var control = g.Root.CreateChild(new ContextOptions { Parallelism = new ParallelHeuristics(4) });
            control.Context.Database.ParallelRuleSetsOverride = true;
            Task.Run(() =>
            {
                probe.Enter(control);
                g.Assert(control, g.Edge, sources[0], targets[0]);
                control.FlushUpdates();
                control.RunRules(meetRules);
            }).Wait();

            Assert.Equal([true, true], meetings.ToArray());
            Assert.Equal(2, probe.CallsOf(control));
            Assert.InRange(probe.StraysOf(control), 1, 2);
            foreach (var sibling in siblings)
                sibling.Dispose();

            // Runs the siblings once the pool is idle and checks each; returns them and the work items the pool
            // completed while they ran.
            (EGraphContext[] Children, long WorkItems) RunSiblings()
            {
                var children = Enumerable.Range(0, Siblings).Select(_ => g.Root.CreateChild()).ToArray();
                Assert.All(children, c => Assert.Same(ParallelHeuristics.Serial, c.Parallelism));
                for (var i = 0; i < Forced; i++)
                    children[i].Context.Database.ParallelRuleSetsOverride = true;

                using var start = new Barrier(Siblings);
                var met = new bool[Siblings];
                var sizes = new long[Siblings];
                Assert.True(WaitForIdlePool(IdlePool, Patience), $"the thread pool was not idle for {IdlePool} within {Patience}");
                var completed = ThreadPool.CompletedWorkItemCount;
                Task.WaitAll(children.Select((sibling, i) => Task.Run(() =>
                {
                    probe.Enter(sibling);
                    met[i] = start.SignalAndWait(Patience);
                    foreach (var s in sources)
                        foreach (var t in targets)
                            g.Assert(sibling, g.Edge, s, t);

                    // A short chain among the targets, different per sibling, so the transitive rule has a few
                    // iterations of work.
                    for (var j = 0; j < 8; j++)
                        g.Assert(sibling, g.Edge, targets[(j * (i + 3)) % Targets], targets[(j * (i + 3) + i + 1) % Targets]);

                    sibling.FlushUpdates();
                    sizes[i] = sibling.Context.Database.TotalSizeEstimate;
                    Assert.False(sibling.Saturate(rules, 64).HitLimit);

                    // A union of two sources, whose rebuild merges their rows of the lattice.
                    var merges = probe.MergesOf(sibling);
                    sibling.StageUnion(sources[0], sources[1 + i]);
                    sibling.FlushUpdates();
                    Assert.True(probe.MergesOf(sibling) > merges, $"sibling {i}'s rebuild merged nothing through the probe");
                    Assert.False(sibling.Saturate(rules, 64).HitLimit);
                })).ToArray());

                // A work item a pool thread ran counts only once that thread is back in the pool, which can be after
                // the task waiting for it has resumed, so the count is read once it covers the siblings' own tasks and
                // the pool has then been idle for Settle.
                Assert.True(SpinWait.SpinUntil(() => ThreadPool.CompletedWorkItemCount - completed >= Siblings && ThreadPool.PendingWorkItemCount == 0, Patience), $"the thread pool did not count the siblings' {Siblings} tasks with nothing queued within {Patience}");
                Assert.True(WaitForIdlePool(Settle, Patience), $"the thread pool was not idle for {Settle} within {Patience} after the siblings ran");
                completed = ThreadPool.CompletedWorkItemCount - completed;

                Assert.All(met, Assert.True);
                for (var i = 0; i < Siblings; i++)
                {
                    var sibling = children[i];
                    Assert.True(sizes[i] > ParallelHeuristics.DbLevelThreshold, $"sibling {i} holds {sizes[i]} rows of its own");
                    Assert.True(probe.CallsOf(sibling) - probe.MergesOf(sibling) >= Sources * Targets, $"sibling {i}: {probe.CallsOf(sibling)} calls, {probe.MergesOf(sibling)} merges");
                    Assert.True(probe.MergesOf(sibling) > 0, $"sibling {i} merged nothing through the probe");
                    Assert.True(probe.StraysOf(sibling) == 0, $"sibling {i} made {probe.StraysOf(sibling)} calls into user code off its task's thread");
                    sibling.CheckInvariants();
                }

                return (children, completed);
            }
        }

        /// <summary>
        /// Waits until the thread pool has completed no work item and held none queued for <paramref name="span"/>.
        /// </summary>
        /// <param name="span">How long the pool must stay idle.</param>
        /// <param name="limit">How long to wait at most.</param>
        /// <returns><c>false</c> when <paramref name="limit"/> passed first.</returns>
        static bool WaitForIdlePool(TimeSpan span, TimeSpan limit)
        {
            var waited = Stopwatch.StartNew();
            var idle = Stopwatch.StartNew();
            var completed = ThreadPool.CompletedWorkItemCount;
            while (idle.Elapsed < span)
            {
                if (waited.Elapsed > limit)
                    return false;

                Thread.Sleep(1);
                var now = ThreadPool.CompletedWorkItemCount;
                if (now != completed || ThreadPool.PendingWorkItemCount != 0)
                {
                    completed = now;
                    idle.Restart();
                }
            }

            return true;
        }

    }

}
