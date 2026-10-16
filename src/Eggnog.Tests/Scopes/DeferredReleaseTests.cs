using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// <see cref="Scope.Dispose"/> returns at once, and the release of a disposed frozen scope waits for every call in
    /// progress on it, including calls that take no guard and make no transient child: an extraction on another thread.
    /// A release that finds another release of its domain in progress is deferred to the end of that release, and a
    /// call that finds one waits for it.
    /// </summary>
    public sealed class DeferredReleaseTests
    {

        [Fact]
        public async Task AFrozenScopeDisposedDuringAnExtractionIsReleasedAfterTheExtraction()
        {
            var t = new NodeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var costs = CostModel.Default.With(t.Lit, node =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return 1;
            });
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            var wrapped = schema.Make(t.Wrap.Of(t.Lit.Of(7L)));
            schema.Freeze();
            var extractor = schema.CreateExtractor(costs);

            var extraction = Task.Factory.StartNew(() => extractor.Extract(wrapped).ToString(), CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            // The extraction is a call in progress on the scope: the release waits for it.
            schema.Dispose();
            Assert.True(schema.IsDisposed);
            Assert.False(schema.Engine.IsDisposed);
            Assert.Equal(1, t.Root.Engine.Context.Context.ChildCount);

            release.Set();
            Assert.Equal("(Wrap (Lit 7))", await extraction);
            Assert.True(schema.Engine.IsDisposed);
            Assert.Empty(t.Root.Children);
            Assert.Equal(0, t.Root.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// A release holds its domain's release lock. A scope disposed while another thread is releasing a scope of the
        /// same domain does not wait for that release: its own release is deferred, and the other release runs it as it
        /// leaves the domain.
        /// </summary>
        [Fact]
        public async Task AScopeDisposedDuringAnotherReleaseOfItsDomainIsReleasedWhenThatReleaseEnds()
        {
            var t = new ScopeTheory();
            var outer = t.Root.CreateChild();
            var kept = outer.CreateChild();
            var disposed = outer.CreateChild();
            Assert.Same(kept.Guard, disposed.Guard);
            using var entered = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();

            // Another thread enters the domain for a release, as a scope's release does, and stays there.
            var releasing = Task.Factory.StartNew(() =>
            {
                var guard = kept.Guard;
                Assert.True(guard.TryEnterForRelease());
                entered.Set();
                proceed.Wait(TimeSpan.FromSeconds(30));
                guard.ExitAfterRelease();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            // The dispose returns without the release lock, and the scope's release waits.
            var disposing = Task.Factory.StartNew(disposed.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await disposing.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(disposed.IsDisposed);
            Assert.False(disposed.Engine.IsDisposed);
            Assert.Equal(2, outer.Engine.Context.Context.ChildCount);

            // The release in progress runs it as it leaves the domain.
            proceed.Set();
            await releasing;
            Assert.True(disposed.Engine.IsDisposed);
            Assert.Equal(1, outer.Engine.Context.Context.ChildCount);
            Assert.False(kept.Engine.IsDisposed);
            kept.Assume(t.Fact.Of("a"));
            Assert.True(kept.Check(t.Fact.Of("a")));
        }

        /// <summary>
        /// A call that finds its domain held by a release on another thread waits for the release to end and then
        /// runs, rather than fail as it would if another call held the domain.
        /// </summary>
        /// <remarks>
        /// The test's thread holds the release for as long as the test needs. It waits until the caller's thread is
        /// blocked (<see cref="ThreadState.WaitSleepJoin"/>) or has ended, holds the release for a further 300 ms, and
        /// ends it only once the caller is blocked again or has ended. The second call starts after the caller's last
        /// wait of its own, and the call's path to the release lock, the guard's failed first try included, makes no
        /// managed wait, so a blocked caller is waiting for the release. A call that stops waiting within the hold fails:
        /// by the end of the hold it has ended or does not block again, where a call that waits as it should only waits
        /// longer.
        /// </remarks>
        [Fact]
        public void ACallThatFindsAReleaseInProgressWaitsForItAndRuns()
        {
            var t = new ScopeTheory();
            var outer = t.Root.CreateChild();
            outer.Assume(t.Fact.Of("a"));
            var guard = outer.Guard;
            using var ready = new ManualResetEventSlim();
            using var go = new ManualResetEventSlim();
            var calling = false;
            var released = false;
            var result = false;
            var returnedAfterRelease = false;
            Exception? error = null;
            var caller = new Thread(() =>
            {
                try
                {
                    Assert.True(outer.Check(t.Fact.Of("a")));
                    var fact = t.Fact.Of("a");
                    ready.Set();
                    go.Wait();
                    Volatile.Write(ref calling, true);
                    result = outer.Check(fact);
                    returnedAfterRelease = Volatile.Read(ref released);
                }
                catch (Exception e)
                {
                    error = e;
                }
            });
            caller.IsBackground = true;
            caller.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));

            // This thread enters the domain for a release, as a scope's release does, and the call finds it there.
            Assert.True(guard.TryEnterForRelease());
            try
            {
                go.Set();
                WaitUntilBlockedOrEnded();
                Assert.True(caller.IsAlive, "the call ended while the release held its domain");

                // The release goes on after the call blocks, and the call must still be waiting when it ends.
                Thread.Sleep(300);
                WaitUntilBlockedOrEnded();
                Assert.True(caller.IsAlive, "the call stopped waiting while the release held its domain");
            }
            finally
            {
                Volatile.Write(ref released, true);
                guard.ExitAfterRelease();
            }

            // Once the release ends, the call runs.
            Assert.True(caller.Join(TimeSpan.FromSeconds(30)), "the call did not end after the release");
            Assert.Null(error);
            Assert.True(result);
            Assert.True(returnedAfterRelease, "the call returned before the release ended");

            // Waits until the call has started and its thread is blocked or has ended.
            void WaitUntilBlockedOrEnded()
            {
                var spin = new SpinWait();
                var deadline = Environment.TickCount64 + 30_000;
                while (Volatile.Read(ref calling) == false || (caller.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) == 0)
                {
                    Assert.True(Environment.TickCount64 < deadline, "the call neither waited nor ended");
                    spin.SpinOnce();
                }
            }
        }

    }

}
