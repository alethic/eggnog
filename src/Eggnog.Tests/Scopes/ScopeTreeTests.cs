using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// The scope tree (§6.7): children, paths, freezing, depth, tag resolution and per-scope parallelism.
    /// </summary>
    public sealed class ScopeTreeTests
    {

        [Fact]
        public void CreateChildMakesAChildWithAPathAndListsIt()
        {
            var t = new ScopeTheory();
            Assert.Null(t.Root.Parent);
            Assert.Same(t.Graph.RootTag, t.Root.Tag);
            Assert.False(t.Root.IsFrozen);

            var outer = t.Root.CreateChild(t.Outer);
            var plain = t.Root.CreateChild();
            var inner = outer.CreateChild(t.Inner);

            Assert.Same(t.Root, outer.Parent);
            Assert.Same(t.Outer, outer.Tag);
            Assert.Null(plain.Tag);
            Assert.False(outer.IsRoot);
            Assert.Matches(@"^root/outer#\d+$", outer.Path);
            Assert.Matches(@"^root/scope#\d+$", plain.Path);
            Assert.StartsWith(outer.Path + "/inner#", inner.Path);
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

            // The query runs in a transient child of the frozen root, which Children does not list (D34).
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
            var child = t.Root.CreateChild(t.Outer);

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
        public void ATreeIsAtMost31Deep()
        {
            var t = new ScopeTheory();
            var scope = t.Root;
            for (var depth = 1; depth <= 31; depth++)
                scope = scope.CreateChild();

            Assert.Contains("31", Assert.Throws<InvalidOperationException>(() => scope.CreateChild()).Message);
        }

        [Fact]
        public void TagsResolveToTheNearestAncestorOrSelf()
        {
            var t = new ScopeTheory();
            t.Root.Assume(t.Fact.Of("root"));
            var a = t.Root.CreateChild(t.Outer);
            a.Assume(t.Fact.Of("a"));
            var b = a.CreateChild(t.Inner);
            b.Assume(t.Fact.Of("b"));
            var c = b.CreateChild(t.Outer);
            c.Assume(t.Fact.Of("c"));

            // From c, outer is c itself; from b, it is a; inner is b from both; the root's tag is the root.
            Assert.True(c.Check(t.Fact.In(t.Outer).Of("c")));
            Assert.True(c.Check(t.Fact.In(t.Outer).Of("a")));
            Assert.False(b.Check(t.Fact.In(t.Outer).Of("b")));
            Assert.True(b.Check(t.Fact.In(t.Outer).Of("a")));
            Assert.True(c.Check(t.Fact.In(t.Inner).Of("b")));
            Assert.False(c.Check(t.Fact.In(t.Inner).Of("c")));
            Assert.True(c.Check(Fact.In(t.Graph.RootTag, t.Fact.Of("root"))));
            Assert.False(c.Check(Fact.In(t.Graph.RootTag, t.Fact.Of("a"))));

            Assert.Same(c, ScopeResolver.Resolve(c, t.Outer, "test"));
            Assert.Same(a, ScopeResolver.Resolve(b, t.Outer, "test"));
            Assert.Same(t.Root, ScopeResolver.Resolve(c, t.Graph.RootTag, "test"));
            Assert.Null(ScopeResolver.TryResolve(a, t.Inner));
            Assert.Equal($"Rule 'r' reads scope 'inner', but '{a.Path}' has no ancestor tagged 'inner'.",
                Assert.Throws<InvalidOperationException>(() => ScopeResolver.Resolve(a, t.Inner, "Rule 'r'")).Message);
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
            Assert.Throws<ArgumentOutOfRangeException>(() => t.Root.CreateChild(options: new ScopeOptions { DegreeOfParallelism = 0 }));
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
    }

}
