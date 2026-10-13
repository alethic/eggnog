using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Concurrency
{

    /// <summary>
    /// Threads at the root: the root is one thread domain, so a call from a second thread while another holds it fails
    /// fast; a rule defined from another thread during a run joins the run's next iteration, while a table definition
    /// throws as long as another thread uses the unfrozen root; and user delegates the engine invokes (primitives,
    /// lattice merges, filters and cost functions, on the calling thread or on pool threads) may not call into a graph.
    /// </summary>
    [Collection(Relations.Tests.ThreadPoolProbeCollection.Name)]
    public sealed class RootThreadingTests
    {

        [Fact]
        public void TwoThreadsOnTheRootThrow()
        {
            using var run = new BlockingRun();
            run.Start();

            var root = run.Graph.Root;
            Assert.Contains("in use on another thread", Assert.Throws<InvalidOperationException>(() => root.Check(run.Seed.Of(1L))).Message);
            Assert.Throws<InvalidOperationException>(() => root.Assume(run.Seed.Of(2L)));
            Assert.Throws<InvalidOperationException>(() => root.Run(run.Rules));
            Assert.Throws<InvalidOperationException>(() => _ = root.IsContradictory);
            Assert.Throws<InvalidOperationException>(() => root.CreateExtractor());
            Assert.Equal("root", root.Path);

            var report = run.Finish();
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(root.Check(run.Done.Of(1L)));
            Assert.False(root.Check(run.Seed.Of(2L)));
        }

        [Fact]
        public async Task ARuleDefinedFromAnotherThreadDuringARunJoinsTheNextIteration()
        {
            var t = new NodeTheory();
            var seen = t.Graph.DefineRelation<long>("seen");
            var started = new ManualResetEventSlim();
            var lateRan = new ManualResetEventSlim();
            var keepGoing = 1;
            t.Rules.DefineRule("tick", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(x, _ =>
                {
                    started.Set();
                    return Volatile.Read(ref keepGoing) == 1;
                });
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            t.Root.Assume(t.Num.Of(0L));

            var run = DedicatedThread.Run(() => t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { IterationLimit = 1_000_000 }));
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            var late = await DedicatedThread.Run(() => t.Rules.DefineRule("late", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(x, _ =>
                {
                    lateRan.Set();
                    return true;
                });
                r.Then(seen.Of(x));
            }));
            Assert.True(lateRan.Wait(TimeSpan.FromSeconds(30)));
            Volatile.Write(ref keepGoing, 0);
            var report = await run;

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Rules[late].IterationsWithMatches > 0);
            Assert.True(t.Root.Check(seen.Of(0L)));
        }

        [Fact]
        public void ATableDefinedFromAnotherThreadDuringARunThrows()
        {
            using var run = new BlockingRun();
            run.Start();
            var e = Assert.Throws<InvalidOperationException>(() => run.Graph.DefineFunction<long, long>("late"));
            Assert.Contains("root", e.Message);
            var count = run.Graph.Tables.Count;
            run.Finish();
            Assert.Equal(count, run.Graph.Tables.Count);
            run.Graph.DefineFunction<long, long>("late");
        }

        [Fact]
        public void APrimitiveThatCallsIntoTheGraphIsRefused()
        {
            var t = new NodeTheory();
            var refusals = new ConcurrentBag<Exception>();
            t.Rules.DefineRule("reentrant filter", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(x, _ =>
                {
                    refusals.Add(Assert.Throws<InvalidOperationException>(() => t.Root.Check(t.Num.Of(1L))));
                    refusals.Add(Assert.Throws<InvalidOperationException>(() => t.Graph.DefineRuleset("from a filter")));
                    return true;
                });
                r.Then(t.Num.Of(Compute.Of(x, v =>
                {
                    refusals.Add(Assert.Throws<InvalidOperationException>(() => t.Root.Assume(t.Num.Of(9L))));
                    refusals.Add(Assert.Throws<InvalidOperationException>(() => new EGraph()));
                    return v + 1;
                })));
            });
            t.Root.Assume(t.Num.Of(1L));

            t.Root.Run(t.Rules);

            Assert.Equal(4, refusals.Count);
            Assert.All(refusals, e => Assert.Equal("Not allowed from a primitive, lattice, filter or cost function", e.Message));
            Assert.True(t.Root.Check(t.Num.Of(2L)));
            Assert.False(t.Root.Check(t.Num.Of(9L)));
        }

        [Fact]
        public void ALatticeMergeThatCallsIntoTheGraphIsRefused()
        {
            var t = new NodeTheory();
            var refusals = new ConcurrentBag<Exception>();
            var best = t.Graph.DefineFunction<string, long>("best", new ValueLattice<long>("reentrant max", (a, b) =>
            {
                refusals.Add(Assert.Throws<InvalidOperationException>(() => t.Root.Check(t.Num.Of(1L))));
                return Math.Max(a, b);
            }));
            t.Rules.DefineRule("write", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(best.Of("k").Is(x));
            });
            t.Root.Assume(best.Of("k").Is(1L), t.Num.Of(5L));

            t.Root.Run(t.Rules);

            Assert.NotEmpty(refusals);
            Assert.All(refusals, e => Assert.Equal("Not allowed from a primitive, lattice, filter or cost function", e.Message));
            Assert.Equal(5L, t.Root.Get(best.Of("k")));
        }

        [Fact]
        public void ACostFunctionThatCallsIntoTheGraphIsRefused()
        {
            var t = new NodeTheory();
            var refusals = new ConcurrentBag<Exception>();
            var lit = t.Root.Make(t.Lit.Of(1L));
            var costs = CostModel.Default.With(t.Lit, node =>
            {
                refusals.Add(Assert.Throws<InvalidOperationException>(() => t.Root.Check(t.Num.Of(1L))));
                return 1;
            });

            Assert.Equal("(Lit 1)", t.Root.Extract(lit, costs).ToString());

            Assert.NotEmpty(refusals);
            Assert.All(refusals, e => Assert.Equal("Not allowed from a primitive, lattice, filter or cost function", e.Message));
        }

        [Fact]
        public void DelegatesOnPoolThreadsAreRefusedToo()
        {
            var t = new NodeTheory(new EGraphOptions { DegreeOfParallelism = 4 });
            t.Graph.Engine.Database.ParallelRuleSetsOverride = true;
            var refusals = new ConcurrentBag<Exception>();
            var threads = new ConcurrentDictionary<int, bool>();
            var tagged = t.Graph.DefineRelation<long>("tagged");
            for (var i = 0; i < 8; i++)
            {
                t.Rules.DefineRule("slow " + i, r =>
                {
                    var x = r.Var<long>("x");
                    r.When(t.Num.Of(x));
                    r.Where(x, _ =>
                    {
                        threads[Environment.CurrentManagedThreadId] = Thread.CurrentThread.IsThreadPoolThread;
                        refusals.Add(Assert.Throws<InvalidOperationException>(() => t.Root.Check(t.Num.Of(1L))));

                        // Hold this rule until a second thread runs one. Under load the pool may otherwise run every
                        // rule inline on the calling thread, and the test would not observe a pool thread at all.
                        SpinWait.SpinUntil(() => threads.Count >= 2, TimeSpan.FromSeconds(10));
                        return true;
                    });
                    r.Then(tagged.Of(x));
                });
            }

            t.Root.Assume(t.Num.Of(1L));
            var caller = Environment.CurrentManagedThreadId;
            t.Root.Run(t.Rules);

            Assert.Equal(8, refusals.Count);
            Assert.All(refusals, e => Assert.Equal("Not allowed from a primitive, lattice, filter or cost function", e.Message));
            Assert.Contains(threads, entry => entry.Key != caller && entry.Value);
            Assert.True(t.Root.Check(tagged.Of(1L)));
        }

        [Fact]
        public void LatticeMergesOnPoolThreadsAreRefusedToo()
        {
            // More than ParallelHeuristics.TableOpThreshold (20,000) writes into one lattice table in one merge: at a
            // degree of parallelism above 1 the table merges them on parallel tasks, so the lattice runs on pool
            // threads.
            const int count = 25_000;
            var t = new NodeTheory(new EGraphOptions { DegreeOfParallelism = 4 });
            var refusals = new ConcurrentDictionary<int, Exception?>();
            var poolThreads = new ConcurrentDictionary<int, bool>();
            var best = t.Graph.DefineFunction<long, long>("best", new ValueLattice<long>("reentrant max", (a, b) =>
            {
                var thread = Environment.CurrentManagedThreadId;
                if (refusals.ContainsKey(thread) == false)
                {
                    refusals[thread] = Record.Exception(() => t.Root.Check(t.Num.Of(1L)));
                    poolThreads[thread] = Thread.CurrentThread.IsThreadPoolThread;

                    // Hold this shard until a second thread merges one. Under load the pool may otherwise leave every
                    // shard to the calling thread, and the test would not observe a pool thread at all.
                    SpinWait.SpinUntil(() => poolThreads.Count >= 2, TimeSpan.FromSeconds(10));
                }

                return Math.Max(a, b);
            }));
            t.Rules.DefineRule("raise", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(best.Of(x).Is(x));
            });
            var facts = new Fact[2 * count];
            for (var i = 0; i < count; i++)
            {
                facts[2 * i] = t.Num.Of(i);
                facts[2 * i + 1] = best.Of((long)i).Is(-1L);
            }

            t.Root.Assume(facts);
            var caller = Environment.CurrentManagedThreadId;
            t.Root.Run(t.Rules);

            Assert.NotEmpty(refusals);
            Assert.All(refusals.Values, e => Assert.Equal("Not allowed from a primitive, lattice, filter or cost function", Assert.IsType<InvalidOperationException>(e).Message));
            Assert.Contains(poolThreads, entry => entry.Key != caller && entry.Value);
            Assert.Equal(count - 1L, t.Root.Get(best.Of(count - 1L)));
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public async Task ACostFunctionOnAPoolThreadIsRefusedToo()
        {
            var t = new NodeTheory(new EGraphOptions { DegreeOfParallelism = 4 });
            var refusals = new ConcurrentBag<Exception?>();
            var onPool = new ConcurrentBag<bool>();
            var lit = t.Root.Make(t.Lit.Of(1L));
            var costs = CostModel.Default.With(t.Lit, node =>
            {
                onPool.Add(Thread.CurrentThread.IsThreadPoolThread);
                refusals.Add(Record.Exception(() => t.Root.Check(t.Num.Of(1L))));
                return 1;
            });

            var text = await Task.Run(() => t.Root.Extract(lit, costs).ToString());

            Assert.Equal("(Lit 1)", text);
            Assert.NotEmpty(refusals);
            Assert.All(onPool, Assert.True);
            Assert.All(refusals, e => Assert.Equal("Not allowed from a primitive, lattice, filter or cost function", Assert.IsType<InvalidOperationException>(e).Message));
        }

    }

}
