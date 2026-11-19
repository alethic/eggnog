using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A scope disposed while a call on it is in progress: the call completes using the scope's classes, and calls in
    /// the scope's live descendants use them too. Outside the scope's subtree its classes are refused as disposed with
    /// <see cref="ArgumentException"/>, and a call on the scope that starts after the disposal is refused at entry with
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    public sealed class DisposalDuringACallTests
    {

        /// <summary>
        /// The interleaving of a frozen scope's extraction on one thread and its disposal on another, made in order on one
        /// thread: the scope is held as the extraction's entry holds it, then disposed, then its class is checked. It
        /// passes in the scope and in its live child, during the call and after it.
        /// </summary>
        [Fact]
        public void AClassOfTheScopeACallHoldsIsUsableAfterTheScopeIsDisposed()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            var a = schema.Make(t.A.Of());
            schema.Freeze();
            var child = schema.CreateChild();

            var held = schema.EnterExtraction();
            try
            {
                schema.Dispose();
                Assert.True(schema.IsDisposed);

                Assert.Null(ClassChecks.Check(schema.Origin, a.Id, null));
                Assert.Equal(a.Id.Cell, ClassChecks.Require(schema, a.Id, nameof(a)));

                Assert.Null(ClassChecks.Check(child.Origin, a.Id, null));
                Assert.True(child.AreEqual(child.Find(a), a));
            }
            finally
            {
                schema.Exit(held);
            }

            Assert.Throws<ObjectDisposedException>(() => schema.Find(a));
            Assert.Throws<ObjectDisposedException>(() => schema.Extract(a));
            Assert.True(child.AreEqual(child.Find(a), a));
        }

        /// <summary>
        /// An extraction of a writable scope catches the scope up after its entry and before it checks the class; a
        /// lattice merge of that catch-up disposes the scope. The extraction completes, and the scope is released after
        /// it.
        /// </summary>
        [Fact]
        public void AnExtractionWhoseScopeIsDisposedAfterItsEntryCompletes()
        {
            Scope? disposed = null;
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A");
            var max = new ValueLattice<long>("disposing max", (x, y) =>
            {
                disposed?.Dispose();
                return Math.Max(x, y);
            });
            var f = graph.DefineFunction<string, long>("f", max);
            graph.Root.Freeze();
            var parent = graph.Root.CreateChild();
            var child = parent.CreateChild();
            var node = child.Make(a.Of());
            child.Assume(f.Of("x").Is(1L));
            var extractor = child.CreateExtractor();
            parent.Assume(f.Of("x").Is(2L));
            disposed = child;

            Assert.Equal("(A)", extractor.Extract(node).ToString());

            Assert.True(child.IsDisposed);
            Assert.True(child.Engine.IsDisposed);
            Assert.Empty(parent.Children);
            Assert.Throws<ObjectDisposedException>(() => extractor.Extract(node));
            Assert.Throws<ObjectDisposedException>(() => extractor.ExtractVariants(node, 2));
            Assert.Contains("which is disposed", Assert.Throws<ArgumentException>(() => parent.CreateChild().Find(node)).Message);
        }

        /// <summary>
        /// A local rule whose definition disposes its owner holds the owner's class: the definition is a call on the
        /// owner that began before the disposal, so it completes. Like a rule defined before the disposal, it stays active
        /// in the owner's live descendants, which keep the owner from being released, and a rule of a live descendant may
        /// hold the class too. A rule of a scope outside the owner's subtree may not.
        /// </summary>
        [Fact]
        public void ALocalRuleDefinedWhileItsOwnerIsDisposedHoldsTheOwnersClass()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var schema = t.Root.CreateChild();
            var a = schema.Make(t.A.Of());
            var b = schema.Make(t.B.Of());
            var child = schema.CreateChild();
            var other = t.Root.CreateChild();
            schema.DefineRule(t.Rules, "before", r =>
            {
                r.When(t.Num.Of(1L));
                r.Then(t.Tag.Of(a));
            });

            var rule = schema.DefineRule(t.Rules, "local", r =>
            {
                schema.Dispose();
                r.When(t.Num.Of(1L));
                r.Then(t.Tag.Of(b));
            });

            Assert.Same(schema, rule.Owner);
            Assert.True(schema.IsDisposed);
            child.DefineRule(t.Rules, "child", r =>
            {
                r.When(t.Tag.Of(a));
                r.Then(t.Num.Of(2L));
            });
            var e = Assert.Throws<RuleDefinitionException>(() => other.DefineRule(t.Rules, "other", r => r.When(t.Tag.Of(a))));
            Assert.Equal($"class {a.Id} came from scope '{schema.Path}', which is disposed", e.Message);
            Assert.Throws<ObjectDisposedException>(() => schema.DefineRule(t.Rules, "after", r => r.When(t.Tag.Of(a))));

            Assert.False(schema.IsReleased);
            child.Assume(t.Num.Of(1L));
            child.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(2, child.CountRows(t.Tag));
            Assert.True(child.Check(t.Num.Of(2L)));
            Assert.False(schema.IsReleased);

            child.Dispose();
            Assert.True(schema.IsReleased);
        }


        /// <summary>
        /// A <c>Write</c> callback that disposes its own scope: the <c>Write</c> is a call on the scope already in
        /// progress, so its writer still unions and uses the scope's classes, and the batch commits. A <c>Write</c> on a
        /// live child of a scope disposed in the callback uses that scope's classes too. A <c>Write</c> on a scope outside
        /// the disposed scope's subtree refuses them, and that batch writes nothing.
        /// </summary>
        [Fact]
        public void AWriteThatDisposesItsScopeStillUsesTheScopesClasses()
        {
            var t = new NodeTheory();
            t.Root.Freeze();
            var scope = t.Root.CreateChild();
            var a = scope.Make(t.A.Of());
            var b = scope.Make(t.B.Of());
            var child = scope.CreateChild();

            scope.Write(w =>
            {
                scope.Dispose();
                w.Union(a, b);
                w.Make(t.Wrap.Of(a));
            });

            Assert.True(scope.IsDisposed);
            Assert.True(child.AreEqual(child.Make(t.A.Of()), child.Make(t.B.Of())));
            Assert.Equal(1, child.CountRows(t.Wrap));

            var other = t.Root.CreateChild();
            var c = other.Make(t.A.Of());
            var d = other.Make(t.B.Of());
            var under = other.CreateChild();
            var outside = t.Root.CreateChild();
            under.Write(w =>
            {
                other.Dispose();
                w.Union(c, d);
                w.Make(t.Wrap.Of(c));
            });
            Assert.True(other.IsDisposed);
            Assert.True(under.AreEqual(c, d));
            Assert.Equal(1, under.CountRows(t.Wrap));

            var e = Assert.Throws<ArgumentException>(() => outside.Write(w => w.Union(c, d)));
            Assert.Equal($"class {c.Id} came from scope '{other.Path}', which is disposed (Parameter 'a')", e.Message);
            Assert.Throws<ArgumentException>(() => outside.Write(w => w.Make(t.Wrap.Of(c))));
            Assert.False(outside.AreEqual(outside.Make(t.A.Of()), outside.Make(t.B.Of())));
            Assert.Equal(0, outside.CountRows(t.Wrap));
        }

        /// <summary>
        /// A run on a child whose <c>Until</c> holds a class of the child's parent, which a lattice merge of the run's
        /// first step disposes: the child is a live descendant of the parent, so the <c>Until</c> reads the class, finds
        /// its condition holds and runs nothing more, and the run completes. The parent is released once the child is
        /// disposed.
        /// </summary>
        [Fact]
        public void AnUntilInAChildsRunReadsAClassOfAParentDisposedDuringTheRun()
        {
            Scope? disposed = null;
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A");
            var tag = graph.DefineRelation<Node>("tag");
            var num = graph.DefineRelation<long>("num");
            var marked = graph.DefineRelation<long>("marked");
            var max = new ValueLattice<long>("disposing max", (x, y) =>
            {
                disposed?.Dispose();
                return Math.Max(x, y);
            });
            var f = graph.DefineFunction<string, long>("f", max);
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("bump", r =>
            {
                r.When(num.Of(1L));
                r.Then(f.Of("x").Is(5L));
            });
            var mark = graph.DefineRuleset("mark");
            mark.DefineRule("mark", r =>
            {
                r.When(num.Of(1L));
                r.Then(marked.Of(1L));
            });
            graph.Root.Freeze();
            var parent = graph.Root.CreateChild();
            var node = parent.Make(a.Of());
            var child = parent.CreateChild();
            child.Assume(f.Of("x").Is(1L));
            child.Assume(num.Of(1L));
            child.Assume(tag.Of(node));
            disposed = parent;

            child.Run(Schedule.Sequence(Schedule.Run(rules), Schedule.Until(Schedule.Run(mark), tag.Of(node))));

            Assert.True(parent.IsDisposed);
            Assert.Equal(5L, child.Get(f.Of("x")));
            Assert.Equal(0, child.CountRows(marked));
            Assert.False(parent.IsReleased);
            Assert.False(parent.Engine.IsDisposed);

            disposed = null;
            child.Dispose();
            Assert.True(parent.IsReleased);
            Assert.True(parent.Engine.IsDisposed);
        }

    }

}
