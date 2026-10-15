using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A scope disposed while a call on it is in progress: the call holds the scope until it returns, so it does not
    /// refuse a class of that very scope because of the disposal, there or in its run's cases. A class of the disposed
    /// scope used by any other call, in any other scope, is refused with <see cref="ArgumentException"/>, and a call
    /// that starts after the disposal is refused at entry with <see cref="ObjectDisposedException"/>.
    /// </summary>
    public sealed class DisposalDuringACallTests
    {

        /// <summary>
        /// The interleaving of a frozen scope's extraction on one thread and its disposal on another, made in order on one
        /// thread: the scope is held as the extraction's entry holds it, then disposed, then its class is checked.
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

                // In another scope the class is dead, though that scope is live and under the disposed one.
                Assert.Equal($"class {a.Id} came from scope '{schema.Path}', which is disposed", ClassChecks.Check(child.Origin, a.Id, null));
                Assert.Contains("which is disposed", Assert.Throws<ArgumentException>(() => child.Find(a)).Message);
            }
            finally
            {
                schema.Exit(held);
            }

            Assert.Throws<ObjectDisposedException>(() => schema.Find(a));
            Assert.Throws<ObjectDisposedException>(() => schema.Extract(a));
            Assert.Contains("which is disposed", Assert.Throws<ArgumentException>(() => child.Find(a)).Message);
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
            Assert.Contains("which is disposed", Assert.Throws<ArgumentException>(() => parent.CreateChild().Find(node)).Message);
        }

        /// <summary>
        /// A local rule whose definition disposes its owner holds the owner's class: the definition is a call on the
        /// owner that began before the disposal, so it completes. Like a rule defined before the disposal, it stays active
        /// in the owner's live descendants until the owner is released. A rule of another scope may not hold that class.
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
            var e = Assert.Throws<RuleDefinitionException>(() => child.DefineRule(t.Rules, "child", r => r.When(t.Tag.Of(a))));
            Assert.Equal($"class {a.Id} came from scope '{schema.Path}', which is disposed", e.Message);
            Assert.Throws<ObjectDisposedException>(() => schema.DefineRule(t.Rules, "after", r => r.When(t.Tag.Of(a))));

            child.Assume(t.Num.Of(1L));
            child.Run(Schedule.Run(t.Rules));
            Assert.Equal(2, child.CountRows(t.Tag));
        }

        /// <summary>
        /// A run whose scope a lattice merge in one of its cases disposes: the run holds its scope, so the cases'
        /// <c>Until</c> condition, checked in the cases, which the run does not enter, still reads a class of that scope.
        /// The same class is refused in a live child of the disposed scope, and a call after the run is refused at entry.
        /// </summary>
        [Fact]
        public void AnUntilInACaseReadsAClassOfTheRunsScopeDisposedDuringTheRun()
        {
            Scope? disposed = null;
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A");
            var tag = graph.DefineRelation<Node>("tag");
            var max = new ValueLattice<long>("disposing max", (x, y) =>
            {
                disposed?.Dispose();
                return Math.Max(x, y);
            });
            var g = graph.DefineFunction<string, long>("g", max);
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var chosen = graph.DefineRelation<string, string>("chosen");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("chosen bumps g", r =>
            {
                r.When(chosen.Of("kind", "x"));
                r.Then(g.Of("k").Is(5L));
            });
            var split = graph.DefineSplit("choose", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(chosen.Of(p, v));
            });
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            var node = scope.Make(a.Of());
            scope.Assume(dom.Of("kind").Is(ValueSet.Of("x", "y")));
            scope.Assume(g.Of("k").Is(1L));
            var child = scope.CreateChild();
            var inCase = Schedule.Sequence(Schedule.Saturate(rules), Schedule.Until(Schedule.Saturate(rules), tag.Of(node)));
            disposed = scope;

            var report = scope.Run(Schedule.Split(split, inCase));

            Assert.Equal(2, report.CasesOpened);
            Assert.True(scope.IsDisposed);
            Assert.Contains("which is disposed", Assert.Throws<ArgumentException>(() => child.Check(tag.Of(node))).Message);
            Assert.Throws<ObjectDisposedException>(() => scope.Check(tag.Of(node)));
        }


        /// <summary>
        /// A <c>Write</c> callback that disposes its own scope: the <c>Write</c> is a call on the scope already in
        /// progress, so its writer still unions and uses the scope's classes, and the batch commits. The same calls in a
        /// <c>Write</c> on a live child of a scope disposed in the callback are refused, and that batch writes nothing.
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
            var e = Assert.Throws<ArgumentException>(() => under.Write(w =>
            {
                other.Dispose();
                w.Union(c, d);
            }));
            Assert.Equal($"class {c.Id} came from scope '{other.Path}', which is disposed (Parameter 'a')", e.Message);
            Assert.Throws<ArgumentException>(() => under.Write(w => w.Make(t.Wrap.Of(c))));
            Assert.False(under.AreEqual(under.Make(t.A.Of()), under.Make(t.B.Of())));
            Assert.Equal(0, under.CountRows(t.Wrap));
        }

        /// <summary>
        /// A run whose scope is disposed while a split step grounds the assumption of its last case, after the case is
        /// made and before the assumption's class of the run's scope is checked; a primitive of the assumption disposes
        /// it in place of another thread. The run holds its scope, so the case assumes the class and the run completes
        /// without a fault. A call after the run is refused at entry.
        /// </summary>
        [Fact]
        public void ASplitAssumptionHoldsAClassOfTheRunsScopeDisposedWhileItIsGrounded()
        {
            Scope? disposed = null;
            var grounded = 0;
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A");
            var dom = graph.DefineFunction<Node, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var chosen = graph.DefineRelation<Node, string>("chosen");
            var disposing = new Primitive<string, string>("disposing", x =>
            {
                if (++grounded == 2)
                    disposed?.Dispose();

                return x;
            }, true);
            var split = graph.DefineSplit("choose", s =>
            {
                var p = s.Var<Node>("p");
                var d = s.Var<ValueSet<string>>("d");
                s.When(d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(chosen.Of(p, disposing.Of(v)));
            });
            var none = graph.DefineRuleset("none");
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            var node = scope.Make(a.Of());
            scope.Assume(dom.Of(node).Is(ValueSet.Of("x", "y")));
            disposed = scope;

            var report = scope.Run(Schedule.Split(split, Schedule.Saturate(none)));

            Assert.Equal(2, grounded);
            Assert.Equal(2, report.CasesOpened);
            Assert.True(scope.IsDisposed);
            Assert.Null(scope.Failure);
            Assert.Throws<ObjectDisposedException>(() => scope.Check(chosen.Of(node, "x")));
        }

        /// <summary>
        /// A run on a child whose <c>Until</c> holds a class of the child's parent, which a lattice merge of the run's
        /// first step disposes: the run does not hold the parent, so the <c>Until</c> refuses the class when it checks it,
        /// after the first step has written.
        /// </summary>
        [Fact]
        public void AnUntilInAChildsRunRefusesAClassOfAParentDisposedDuringTheRun()
        {
            Scope? disposed = null;
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A");
            var tag = graph.DefineRelation<Node>("tag");
            var num = graph.DefineRelation<long>("num");
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
            graph.Root.Freeze();
            var parent = graph.Root.CreateChild();
            var node = parent.Make(a.Of());
            var child = parent.CreateChild();
            child.Assume(f.Of("x").Is(1L));
            child.Assume(num.Of(1L));
            disposed = parent;

            var e = Assert.Throws<ArgumentException>(() => child.Run(Schedule.Sequence(Schedule.Run(rules), Schedule.Until(Schedule.Run(rules), tag.Of(node)))));

            Assert.Equal($"class {node.Id} came from scope '{parent.Path}', which is disposed (Parameter 'term')", e.Message);
            Assert.True(parent.IsDisposed);
            Assert.Equal(5L, child.Get(f.Of("x")));
        }

    }

}
