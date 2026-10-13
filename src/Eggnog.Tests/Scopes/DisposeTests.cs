using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Disposal (D40): <see cref="Scope.Dispose"/> never throws and makes a scope unusable at once; its engine state and
    /// local rules are released once no live child (transient query children included) and no call on another thread of
    /// its domain defers it.
    /// </summary>
    public sealed class DisposeTests
    {

        [Fact]
        public void DisposeNeverThrowsAndASecondCallDoesNothing()
        {
            var t = new ScopeTheory();
            var child = t.Root.CreateChild(t.Outer);
            var grandchild = child.CreateChild();
            child.Assume(t.Fact.Of("a"));

            // A scope with a live child: disposal never throws; the release waits for the child.
            child.Dispose();
            Assert.True(child.IsDisposed);
            Assert.False(child.Engine.IsDisposed);
            child.Dispose();

            Assert.Throws<ObjectDisposedException>(() => child.Check(t.Fact.Of("a")));
            Assert.Throws<ObjectDisposedException>(() => child.Assume(t.Fact.Of("b")));
            Assert.Throws<ObjectDisposedException>(() => child.Run(t.Rules));
            Assert.Throws<ObjectDisposedException>(() => child.CreateChild());
            Assert.Throws<ObjectDisposedException>(child.Freeze);
            Assert.Equal(child.Path, child.ToString());

            // The grandchild still works: its parent's state is kept for it.
            Assert.True(grandchild.Check(t.Fact.Of("a")));
            grandchild.Dispose();
            Assert.True(child.Engine.IsDisposed);
            Assert.Empty(t.Root.Children);
            Assert.Equal(0, t.Root.Engine.Context.Context.ChildCount);
        }

        [Fact]
        public void TheRootsDisposeDoesNothing()
        {
            var t = new ScopeTheory();
            t.Root.Dispose();
            Assert.False(t.Root.IsDisposed);
            t.Root.Assume(t.Fact.Of("a"));
            Assert.True(t.Root.Check(t.Fact.Of("a")));
        }

        [Fact]
        public void ADisposedScopesClassesDie()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var child = t.Root.CreateChild();
            var a = child.Make(t.A.Of());
            var grandchild = child.CreateChild();
            Assert.True(grandchild.AreEqual(a, a));

            child.Dispose();
            Assert.Contains("disposed", Assert.Throws<ArgumentException>(() => grandchild.Find(a)).Message);
        }

        [Fact]
        public async Task AFrozenSchemaDisposedWhileQueriesRunIsReleasedAfterTheLast()
        {
            var t = new ScopeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var arrivals = 0;
            t.Rules.DefineRule("wait", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Where(x, _ =>
                {
                    if (Interlocked.Increment(ref arrivals) == 8)
                        entered.Set();

                    release.Wait(TimeSpan.FromSeconds(30));
                    return true;
                });
                r.Then(t.Seen.Of(x));
            });
            t.Root.Freeze();
            var schema = t.Root.CreateChild(t.Outer);
            var local = schema.DefineRule(t.Rules, "local", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Seen.Of(x));
                r.Then(t.Fact.Of("local " + "seen"));
            });
            schema.Freeze();
            var description = local.Compiled.Variants.Default!.Description;
            Assert.True(t.Graph.VariantsByDescription.ContainsKey(description));

            var queries = new List<Task<bool>>();
            for (var i = 0; i < 8; i++)
            {
                var value = "q" + i;
                queries.Add(Task.Factory.StartNew(() =>
                {
                    using var query = schema.CreateChild();
                    query.Assume(t.Fact.Of(value));
                    query.Run(Schedule.Saturate(t.Rules));
                    return query.Check(t.Seen.Of(value), t.Fact.Of("local seen"));
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            }

            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            schema.Dispose();
            Assert.True(schema.IsDisposed);
            Assert.False(schema.Engine.IsDisposed);
            Assert.True(t.Graph.VariantsByDescription.ContainsKey(description));

            release.Set();
            foreach (var query in queries)
                Assert.True(await query);

            Assert.True(schema.Engine.IsDisposed);
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(description));
            Assert.Empty(t.Root.Children);
            Assert.Equal(0, t.Root.Engine.Context.Context.ChildCount);
        }

        [Fact]
        public async Task AFrozenScopeDisposedDuringAQueryIsReleasedAfterTheQuery()
        {
            var t = new ScopeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var blocking = t.Graph.DefineQuery("blocking", q =>
            {
                var x = q.Var<string>("x");
                q.When(t.Fact.Of(x));
                q.Where(x, _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return true;
                });
            });
            t.Root.Freeze();
            var schema = t.Root.CreateChild(t.Outer);
            schema.Assume(t.Fact.Of("a"));
            schema.Freeze();

            var count = Task.Factory.StartNew(() => schema.Count(blocking), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            schema.Dispose();
            Assert.False(schema.Engine.IsDisposed);

            release.Set();
            Assert.Equal(1, await count);
            Assert.True(schema.Engine.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => schema.Count(blocking));
        }

        [Fact]
        public async Task AWritableScopeDisposedFromAnotherThreadIsReleasedWhenItsDomainsCallEnds()
        {
            var t = new ScopeTheory();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            t.DefineBlocking(entered, release);
            t.Root.Assume(t.Fact.Of("a"));
            var child = t.Root.CreateChild(t.Outer);

            var run = Task.Factory.StartNew(() => t.Root.Run(t.Rules), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            // The child shares the root's domain, which the run holds on another thread: the release waits for it.
            child.Dispose();
            Assert.True(child.IsDisposed);
            Assert.False(child.Engine.IsDisposed);

            release.Set();
            Assert.Equal(RunStopReason.Completed, (await run).StopReason);
            Assert.True(child.Engine.IsDisposed);
            Assert.Empty(t.Root.Children);
        }

        [Fact]
        public void ADisposeFromAQueryCallbackIsReleasedWhenTheCallEnds()
        {
            var t = new ScopeTheory();
            var all = t.Graph.DefineQuery("all", q => q.When(t.Fact.Of(q.Var<string>("x"))));
            var child = t.Root.CreateChild();
            child.Assume(t.Fact.Of("a"), t.Fact.Of("b"));

            var seen = 0;
            child.ForEachMatch(all, _ =>
            {
                child.Dispose();
                Assert.False(child.Engine.IsDisposed);
                seen++;
                return true;
            });

            Assert.Equal(2, seen);
            Assert.True(child.Engine.IsDisposed);
        }


        [Fact]
        public void ADisposeFromAPrimitiveIsDeferredUntilTheRunEnds()
        {
            var t = new ScopeTheory();
            Scope? child = null;
            t.Rules.DefineRule("disposes", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Where(x, _ =>
                {
                    child!.Dispose();
                    return true;
                });
                r.Then(t.Seen.Of(x));
            });
            child = t.Root.CreateChild();
            child.Assume(t.Fact.Of("a"));

            var report = child.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(child.IsDisposed);
            Assert.True(child.Engine.IsDisposed);
            Assert.Empty(t.Root.Children);
        }

        /// <summary>
        /// R3: an open <see cref="CaseSet"/> defers its parent's release (D40): its cases are children of the parent.
        /// </summary>
        [Fact]
        public void AnOpenCaseSetDefersItsParentsRelease()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            var cases = parent.OpenCases();
            var @case = cases.Add();
            @case.Assume(t.Fact.Of("a"));

            parent.Dispose();
            Assert.True(parent.IsDisposed);
            Assert.False(parent.Engine.IsDisposed);
            Assert.True(@case.Check(t.Fact.Of("a")));                       // the case still works
            Assert.Throws<ObjectDisposedException>(() => cases.Lift());      // its parent cannot be lifted into

            cases.Dispose();
            Assert.True(@case.IsDisposed);
            Assert.True(@case.Engine.IsDisposed);
            Assert.True(parent.Engine.IsDisposed);
            Assert.Empty(t.Root.Children);
            Assert.Equal(0, t.Root.Engine.Context.Context.ChildCount);
        }
    }

}
