using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// The scope tree: children, paths, freezing and per-scope parallelism; what a frozen scope refuses, and the
    /// structural calls a run observer may not make.
    /// </summary>
    public sealed class ScopeTreeTests
    {

        [Fact]
        public void CreateChildMakesAChildWithAPathAndListsIt()
        {
            var t = new ScopeTheory();
            Assert.Null(t.Root.Parent);
            Assert.False(t.Root.IsFrozen);

            var outer = t.Root.CreateChild();
            var plain = t.Root.CreateChild();
            var inner = outer.CreateChild();

            Assert.Same(t.Root, outer.Parent);
            Assert.False(outer.IsRoot);
            Assert.Matches(@"^root/scope#\d+$", outer.Path);
            Assert.Matches(@"^root/scope#\d+$", plain.Path);
            Assert.NotEqual(outer.Path, plain.Path);
            Assert.StartsWith(outer.Path + "/scope#", inner.Path);
            Assert.Equal(new[] { outer, plain }, t.Root.Children);
            Assert.Equal(new[] { inner }, outer.Children);
            Assert.Equal(inner.Path, inner.ToString());

            plain.Dispose();
            Assert.Equal(new[] { outer }, t.Root.Children);
        }

        [Fact]
        public void TransientQueryChildrenAreNotListed()
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
            t.Root.Assume(t.Fact.Of("a"));
            t.Root.Freeze();

            var query = Task.Factory.StartNew(() => t.Root.Count(blocking), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            // The query runs in a transient child of the frozen root, which Children does not list.
            Assert.Empty(t.Root.Children);
            Assert.Equal(1, t.Root.Engine.Context.Context.ChildCount);

            release.Set();
            Assert.Equal(1, query.GetAwaiter().GetResult());
            Assert.Equal(0, t.Root.Engine.Context.Context.ChildCount);
        }

        [Fact]
        public void FreezingUnderAWritableParentThrows()
        {
            var t = new ScopeTheory();
            var child = t.Root.CreateChild();

            Assert.Equal("A scope can be frozen only once its parent is", Assert.Throws<InvalidOperationException>(child.Freeze).Message);
            Assert.False(child.IsFrozen);

            t.Root.Freeze();
            Assert.True(t.Root.IsFrozen);
            t.Root.Freeze();
            child.Freeze();
            Assert.True(child.IsFrozen);
        }

        [Fact]
        public void AFrozenScopeRefusesWritesRunsAndLocalRules()
        {
            var t = new ScopeTheory();
            t.Root.Freeze();

            Assert.Contains("frozen", Assert.Throws<InvalidOperationException>(() => t.Root.Assume(t.Fact.Of("a"))).Message);
            Assert.Contains("frozen", Assert.Throws<InvalidOperationException>(() => t.Root.Run(t.Rules)).Message);
            Assert.Equal("Scope 'root' is frozen; its local rules are fixed",
                Assert.Throws<InvalidOperationException>(() => t.Root.DefineRule(t.Rules, "late", r => r.When(t.Fact.Of("a")))).Message);
            Assert.False(t.Root.Check(t.Fact.Of("a")));
        }

        [Fact]
        public void ScopeOptionsReachTheContext()
        {
            var t = new ScopeTheory(new EGraphOptions { DegreeOfParallelism = 2 });
            var inherited = t.Root.CreateChild();
            var own = t.Root.CreateChild(options: new ScopeOptions { DegreeOfParallelism = 3 });
            Assert.Equal(2, inherited.Engine.Context.Parallelism.DegreeOfParallelism);
            Assert.Equal(3, own.Engine.Context.Parallelism.DegreeOfParallelism);

            t.Root.Freeze();
            var serial = t.Root.CreateChild();
            var four = t.Root.CreateChild(options: new ScopeOptions { DegreeOfParallelism = 4 });
            Assert.Equal(1, serial.Engine.Context.Parallelism.DegreeOfParallelism);
            Assert.Equal(4, four.Engine.Context.Parallelism.DegreeOfParallelism);
            var all = t.Root.CreateChild(options: new ScopeOptions { DegreeOfParallelism = 0 });
            Assert.Equal(Math.Max(1, Environment.ProcessorCount), all.Engine.Context.Parallelism.DegreeOfParallelism);
            Assert.Throws<ArgumentOutOfRangeException>(() => t.Root.CreateChild(options: new ScopeOptions { DegreeOfParallelism = -1 }));
        }

        [Fact]
        public void ADegreeOfParallelismOfZeroIsEveryProcessor()
        {
            var t = new ScopeTheory(new EGraphOptions { DegreeOfParallelism = 0 });
            Assert.Equal(Math.Max(1, Environment.ProcessorCount), t.Root.Engine.Context.Parallelism.DegreeOfParallelism);
            Assert.Equal(Math.Max(1, Environment.ProcessorCount), t.Root.CreateChild().Engine.Context.Parallelism.DegreeOfParallelism);
            Assert.Throws<ArgumentOutOfRangeException>(() => new EGraph(new EGraphOptions { DegreeOfParallelism = -1 }));
        }

        [Fact]
        public void ChildrenOfAFrozenScopeHaveTheirOwnDomainAndChildrenOfAWritableOneShareIt()
        {
            var t = new ScopeTheory();
            var writableChild = t.Root.CreateChild();
            Assert.Same(t.Root.Guard, writableChild.Guard);

            t.Root.Freeze();

            // Freezing gave the live child subtree its own domain; new children get their own too.
            Assert.NotSame(t.Root.Guard, writableChild.Guard);
            var a = t.Root.CreateChild();
            var b = t.Root.CreateChild();
            Assert.NotSame(a.Guard, b.Guard);
            Assert.NotSame(writableChild.Guard, a.Guard);
            var grandchild = a.CreateChild();
            Assert.Same(a.Guard, grandchild.Guard);

            // Siblings under a frozen scope run on two threads at once.
            var threads = Support.DedicatedThreads.For(2, 2, i =>
            {
                if (i == 0)
                    a.Assume(t.Fact.Of("a"));
                else
                    b.Assume(t.Fact.Of("b"));
            });
            Assert.Equal(2, threads);
            Assert.True(a.Check(t.Fact.Of("a")));
            Assert.False(a.Check(t.Fact.Of("b")));
            Assert.True(b.Check(t.Fact.Of("b")));
        }

        [Fact]
        public void FreezingFromInsideACallOfTheDomainIsRefused()
        {
            var t = new ScopeTheory();
            var all = t.Graph.DefineQuery("all", q => q.When(t.Fact.Of(q.Var<string>("x"))));
            t.Root.Assume(t.Fact.Of("a"));
            var child = t.Root.CreateChild();

            Exception? refused = null;
            child.ForEachMatch(all, _ =>
            {
                refused = Record.Exception(t.Root.Freeze);
                return true;
            });

            Assert.IsType<InvalidOperationException>(refused);
            Assert.False(t.Root.IsFrozen);
            Assert.Same(t.Root.Guard, child.Guard);
        }

        /// <summary>
        /// A host write on a frozen scope is an <see cref="InvalidOperationException"/>; so is <c>DefineRule</c>, whose
        /// message says the scope's local rules are fixed.
        /// </summary>
        [Fact]
        public void AFrozenScopeRefusesWritesAndLocalRules()
        {
            var t = new ScopeTheory();
            t.Root.Freeze();
            var outer = t.Root.CreateChild();
            outer.Freeze();

            Assert.Throws<InvalidOperationException>(() => outer.Assume(t.Fact.Of("a")));
            Assert.Throws<InvalidOperationException>(() => outer.Write(w => w.Assume(t.Fact.Of("a"))));
            var error = Assert.Throws<InvalidOperationException>(() => outer.DefineRule(t.Rules, "late", r => r.Then(t.Seen.Of("a"))));
            Assert.Equal("Scope 'root/scope#1' is frozen; its local rules are fixed", error.Message);
        }

        /// <summary>
        /// A write, run or structural call from a run observer throws <see cref="InvalidOperationException"/>; only
        /// reads are allowed there. <c>CreateChild</c> is a structural call, and the running scope gains no child.
        /// </summary>
        [Fact]
        public void CreateChildFromARunObserverIsRefused()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("seen", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.Of(x));
                r.Then(t.Seen.Of(x));
            });
            var scope = t.Root.CreateChild();
            scope.Assume(t.Fact.Of("a"));

            Exception? refused = null;
            var created = false;
            scope.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = _ =>
                {
                    if (refused is not null || created)
                        return;

                    refused = Record.Exception(() => scope.CreateChild());
                    created = refused is null;
                },
            });

            Assert.IsType<InvalidOperationException>(refused);
            Assert.Empty(scope.Children);
        }

    }

}
