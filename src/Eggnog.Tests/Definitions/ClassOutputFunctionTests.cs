using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Definitions
{

    /// <summary>
    /// Functions whose output is a class, as egglog's function of an e-class sort: a class written for a key is held
    /// without union, two different classes for one key are a conflict unless a merge makes them equal, the rebuild
    /// keeps the output canonical, and <c>New</c> and <c>Old</c> pick a class. Ported from egglog's
    /// <c>tests/set_sort_function.egg</c> at 5e655cae, with what the batch, rules, children and splits do with them.
    /// </summary>
    public sealed class ClassOutputFunctionTests
    {

        /// <summary>
        /// tests/set_sort_function.egg: <c>(set (bar) (baz))</c>, a union of <c>baz</c> and <c>qux</c>, then
        /// <c>(set (bar) (qux))</c> holds, and <c>(set (bar) (quux))</c> fails.
        /// </summary>
        [Fact]
        public void AClassWrittenAgainAfterAUnionHoldsAndAnotherConflicts()
        {
            var t = new NodeTheory();
            var bar = t.Graph.DefineFunction<Node>("bar");

            t.Root.Assume(bar.Of().Is(t.A.Of()));
            t.Root.Union(t.Root.Make(t.A.Of()), t.Root.Make(t.B.Of()));
            var version = t.Root.Version;
            t.Root.Assume(bar.Of().Is(t.B.Of()));
            Assert.Equal(version, t.Root.Version);

            Assert.Throws<FactConflictException>(() => t.Root.Assume(bar.Of().Is(t.Lit.Of(3L))));
            Assert.True(t.Root.AreEqual(t.Root.Get(bar.Of()), t.B.Of()));
            Assert.False(t.Root.Check(Fact.Exists(t.Lit.Of(3L))));
        }

        /// <summary>
        /// A rule writes a class into a function and another reads it back; the rebuild keeps the output canonical
        /// when the class merges.
        /// </summary>
        [Fact]
        public void RulesWriteAndReadClassesAndTheRebuildKeepsThemCanonical()
        {
            var t = new NodeTheory();
            var parent = t.Graph.DefineFunction<Node, Node>("parent");
            t.Rules.DefineRule("parent of the wrapped", r =>
            {
                var (x, y) = r.Vars<Node, Node>("x", "y");
                r.When(x.Is(t.Wrap.Of(y)));
                r.Then(parent.Of(y).Is(x));
            });
            t.Rules.DefineRule("tag parents", r =>
            {
                var (y, p) = r.Vars<Node, Node>("y", "p");
                r.When(p.Is(parent.Of(y)));
                r.Then(t.Tag.Of(p));
            });
            var a = t.Root.Make(t.A.Of());
            var wrapped = t.Root.Make(t.Wrap.Of(a));

            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(t.Tag.Of(wrapped)));
            Assert.True(t.Root.AreEqual(t.Root.Get(parent.Of(a)), t.Wrap.Of(a)));

            var b = t.Root.Make(t.B.Of());
            t.Root.Union(wrapped, b);
            Assert.Equal(t.Root.Find(b), t.Root.Find(t.Root.Get(parent.Of(a))));
        }

        /// <summary>
        /// A union that makes two keys equal leaves one row when their classes are equal, and is a conflict when they
        /// differ, as egglog's assert-equal merge panics; the batch refuses it before anything is written. A later union
        /// of the two classes in the same batch is egglog's next command, which does not save it: (union (A) (B)) fails,
        /// (union (Lit 1) (Lit 2)) stands.
        /// </summary>
        [Fact]
        public void AUnionOfKeysNeedsTheirClassesEqual()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, Node>("f");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            t.Root.Assume(f.Of(a).Is(one), f.Of(b).Is(two));

            Assert.Throws<FactConflictException>(() => t.Root.Union(a, b));
            Assert.False(t.Root.AreEqual(a, t.B.Of()));
            Assert.Equal(2, t.Root.CountRows(f));

            t.Root.Write(w =>
            {
                Assert.Throws<FactConflictException>(() => w.Union(a, b));
                w.Union(one, two);
            });
            Assert.False(t.Root.AreEqual(a, b));
            Assert.True(t.Root.AreEqual(one, two));
            Assert.Equal(2, t.Root.CountRows(f));
            Assert.False(t.Root.IsFaulted);

            // With the classes one, the union of the keys leaves one row.
            t.Root.Union(a, b);
            Assert.Equal(1, t.Root.CountRows(f));
        }

        /// <summary>
        /// Classes that only congruence makes one do not agree in time: the rebuild compares the outputs
        /// <c>(Wrap 1)</c> and <c>(Wrap 2)</c> as its first pass finds them, before it merges them, and its assert-equal
        /// merge panics, as egglog's does. The batch refuses that before anything is written, rather than fault the
        /// scope.
        /// </summary>
        [Fact]
        public void ClassesThatOnlyCongruenceMakesOneAreAConflict()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, Node>("f");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            t.Root.Assume(f.Of(a).Is(t.Wrap.Of(one)), f.Of(b).Is(t.Wrap.Of(two)));

            Assert.Throws<FactConflictException>(() => t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Union(one, two);
            }));

            Assert.False(t.Root.IsFaulted);
            Assert.False(t.Root.AreEqual(a, t.B.Of()));
            Assert.Equal(2, t.Root.CountRows(f));
        }

        /// <summary>
        /// Rows that meet only through congruence are compared in a later pass, after the congruences that make them
        /// meet: one <c>Union(a, b)</c> makes <c>(Wrap a)</c> and <c>(Wrap b)</c> one class, and then the rows
        /// <c>f((Wrap a)) = (Wrap a)</c> and <c>f((Wrap b)) = (Wrap b)</c> meet holding it, which egglog accepts.
        /// </summary>
        [Fact]
        public void RowsThatMeetThroughCongruenceAreComparedAfterIt()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, Node>("f");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(f.Of(t.Wrap.Of(a)).Is(t.Wrap.Of(a)), f.Of(t.Wrap.Of(b)).Is(t.Wrap.Of(b)));

            t.Root.Union(a, b);

            Assert.False(t.Root.IsFaulted);
            Assert.Equal(1, t.Root.CountRows(f));
            Assert.True(t.Root.AreEqual(t.Root.Get(f.Of(t.Wrap.Of(a))), t.Wrap.Of(b)));
        }

        /// <summary>
        /// A union merges the row it keeps, the smaller class's, as the old class with the other as the new, as egglog's
        /// rebuild does.
        /// </summary>
        [Fact]
        public void AUnionMergesNewAndOldClassesInTheRebuildsOrder()
        {
            var t = new NodeTheory();
            var newest = t.Graph.DefineFunction<Node, Node>("newest", FunctionMerge.New<Node>());
            var oldest = t.Graph.DefineFunction<Node, Node>("oldest", FunctionMerge.Old<Node>());
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            t.Root.Assume(newest.Of(a).Is(one), newest.Of(b).Is(two), oldest.Of(a).Is(one), oldest.Of(b).Is(two));

            t.Root.Union(b, a);

            Assert.True(t.Root.AreEqual(t.Root.Get(newest.Of(a)), t.Lit.Of(2L)));
            Assert.True(t.Root.AreEqual(t.Root.Get(oldest.Of(a)), t.Lit.Of(1L)));
        }

        [Fact]
        public void ARowOfAFunctionOfASortIsSerializedInItsClassAndDeleted()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, Node>("f");
            t.Root.Assume(f.Of(1L).Is(t.A.Of()));

            var graph = t.Root.Serialize();
            var row = graph.Nodes.Single(n => n.Op == "f");
            var term = graph.Nodes.Single(n => n.Op == "A");
            Assert.Equal(term.Class, row.Class);

            t.Root.Delete(f.Of(1L));
            Assert.Equal(0, t.Root.CountRows(f));
        }

        [Fact]
        public void AChildsRuleThatWritesAnotherClassFailsTheRun()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, Node>("f");
            t.Rules.DefineRule("other", r => r.Then(f.Of(1L).Is(t.B.Of())));
            t.Root.Assume(f.Of(1L).Is(t.A.Of()));
            var child = t.Root.CreateChild();

            Assert.Throws<RuleEvaluationException>(() => child.Run(t.Rules));

            Assert.True(child.IsFaulted);
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public void ABatchWritesAClassAfterItMergedClasses()
        {
            // (union (A) (B)) (set (f 1) (A)) (check (= (f 1) (B))): the write comes after the union's rebuild, as egglog's
            // next command does.
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, Node>("f");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());

            t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(f.Of(1L).Is(t.A.Of()));
            });

            Assert.True(t.Root.AreEqual(t.Root.Get(f.Of(1L)), t.B.Of()));
            Assert.Equal(1, t.Root.CountRows(f));

            // New and Old compare no classes, so the batch may write them after its merges.
            var newest = t.Graph.DefineFunction<long, Node>("newest", FunctionMerge.New<Node>());
            t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(newest.Of(1L).Is(t.A.Of()));
            });
            Assert.True(t.Root.AreEqual(t.Root.Get(newest.Of(1L)), t.B.Of()));
        }

        [Fact]
        public void ACallThatNeedsAnEarlierMergeComesAfterTheBatchFlushesIt()
        {
            // egglog runs a write's calls as commands, each after the last one's rebuild: the write of f, which holds
            // classes, comes after the batch flushes the union; a later call that fails writes nothing, and the calls
            // before it stand.
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, Node>("f");
            var g = t.Graph.DefineFunction<long, long>("g");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());

            Assert.Throws<InvalidOperationException>(() => t.Root.Write(w =>
            {
                w.Union(a, b);
                w.Assume(f.Of(1L).Is(t.A.Of()));
                w.Assume(g.Of(1L).Is(g.Of(2L)));
            }));

            Assert.True(t.Root.AreEqual(a, b));
            Assert.True(t.Root.AreEqual(t.Root.Get(f.Of(1L)), b));
            Assert.Equal(0, t.Root.CountRows(g));
        }

        [Fact]
        public void NewAndOldPickAClass()
        {
            var t = new NodeTheory();
            var newest = t.Graph.DefineFunction<long, Node>("newest", FunctionMerge.New<Node>());
            var oldest = t.Graph.DefineFunction<long, Node>("oldest", FunctionMerge.Old<Node>());
            t.Root.Assume(newest.Of(1L).Is(t.A.Of()), oldest.Of(1L).Is(t.A.Of()));

            t.Root.Assume(newest.Of(1L).Is(t.B.Of()), oldest.Of(1L).Is(t.B.Of()));

            Assert.True(t.Root.AreEqual(t.Root.Get(newest.Of(1L)), t.B.Of()));
            Assert.True(t.Root.AreEqual(t.Root.Get(oldest.Of(1L)), t.A.Of()));
        }

        /// <summary>
        /// A child writes a function of a sort without a merge, as any assert-equal function; below the root, a union in
        /// which the class a row of a function with <c>New</c> or <c>Old</c> outputs would lose is refused, since the
        /// child's rebuild would rewrite the row, which only the root writes.
        /// </summary>
        [Fact]
        public void BelowTheRootAnAssertEqualFunctionIsWrittenAndANewOrOldOutputIsNotMergedAway()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, Node>("f");
            var newest = t.Graph.DefineFunction<long, Node>("newest", FunctionMerge.New<Node>());
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(newest.Of(1L).Is(b));
            var child = t.Root.CreateChild();

            child.Assume(f.Of(1L).Is(t.A.Of()));
            Assert.True(child.AreEqual(child.Get(f.Of(1L)), t.A.Of()));
            var e = Assert.Throws<InvalidOperationException>(() => child.Union(a, b));

            Assert.Contains("'newest' has the merge 'new'", e.Message, StringComparison.Ordinal);
            Assert.False(child.IsFaulted);
            Assert.False(child.AreEqual(a, t.B.Of()));
        }

        [Fact]
        public void ARewriteCannotMakeAFunctionApplication()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, Node>("f");

            var e = Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("rw", r =>
            {
                var x = r.Var<Node>("x");
                r.Rewrite(t.Wrap.Of(x), f.Of(x));
            }));

            Assert.Contains("'f' is a function, which a head does not read", e.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AFunctionOfASortIsNotReadInAnotherScope()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, Node>("f");

            Assert.Throws<ArgumentException>(() => f.In(t.Graph.RootTag));
        }

        /// <summary>
        /// A split rule may assume a class of a function of a sort, as a host write makes it, alongside a union, in either
        /// order: a case writes its functions before its unions. A class every case assumes is lifted to the parent.
        /// </summary>
        [Fact]
        public void ASplitAssumesAClassOfAFunctionAndTheLiftKeepsIt()
        {
            var t = new NodeTheory();
            var choice = t.Graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var picked = t.Graph.DefineFunction<string, Node>("picked");
            var split = t.Graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)), t.B.Of().Is(t.Lit.Of(v)), picked.Of(k).Is(t.A.Of()));
            });
            var a = t.Root.Make(t.A.Of());
            t.Root.Assume(choice.Of("k").Is(Set.Of(1L, 2L)));

            var report = t.Root.Run(Schedule.Split(split, Schedule.Run(t.Rules)));

            Assert.Equal(2, report.CasesOpened);
            Assert.False(t.Root.IsFaulted);
            Assert.True(t.Root.AreEqual(t.Root.Get(picked.Of("k")), t.A.Of()));
            Assert.Equal(t.Root.Find(a), t.Root.Find(t.Root.Get(picked.Of("k"))));
        }

        /// <summary>
        /// A case whose assumption makes two rows of a function of a sort meet holding different classes fails the run, and
        /// faults its scope, as a conflicting assumption does.
        /// </summary>
        [Fact]
        public void ACaseWhoseAssumptionMakesClassesMeetFailsTheRun()
        {
            var t = new NodeTheory();
            var choice = t.Graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var f = t.Graph.DefineFunction<Node, Node>("f");
            var split = t.Graph.DefineSplit("join", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)), t.A.Of().Is(t.B.Of()));
            });
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(f.Of(a).Is(t.Lit.Of(1L)), f.Of(b).Is(t.Lit.Of(2L)), choice.Of("k").Is(Set.Of(1L, 2L)));

            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(Schedule.Split(split, Schedule.Run(t.Rules))));

            Assert.IsType<FactConflictException>(e.InnerException);
            Assert.True(t.Root.IsFaulted);
        }

    }

}
