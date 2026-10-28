using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Deletion
{

    /// <summary>
    /// egglog's <c>delete</c>, from host calls and rule heads: the row of a key goes and nothing else; a key with no row
    /// deletes nothing; an iteration deletes before it writes; a deletion is no change for saturation. And where a scope
    /// may delete: not with live descendants, not under a writable parent, not in a run that splits; and a deletion keeps
    /// the scope's mirror under its own tag equal to the table.
    /// </summary>
    public sealed class DeleteTests
    {

        static readonly FunctionMerge<long> Sum = FunctionMerge.Of<long>("sum", (a, b) => checked(a + b));

        [Fact]
        public void AHostDeleteRemovesTheRowOfAFunctionOrARelation()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            t.Root.Assume(f.Of(1L).Is(10L), f.Of(2L).Is(20L), t.Num.Of(1L), t.Num.Of(2L));
            var version = t.Root.Version;

            t.Root.Delete(f.Of(1L));
            t.Root.Delete(t.Num.Of(2L));

            Assert.False(t.Root.TryGet(f.Of(1L), out _));
            Assert.Equal(20L, t.Root.Get(f.Of(2L)));
            Assert.True(t.Root.Check(t.Num.Of(1L)));
            Assert.False(t.Root.Check(t.Num.Of(2L)));
            Assert.NotEqual(version, t.Root.Version);
        }

        /// <summary>
        /// A deletion goes by key, so a subsumed row goes with its subsumption, and the row asserted again is not
        /// subsumed, as in egglog.
        /// </summary>
        [Fact]
        public void DeletingASubsumedRowForgetsTheSubsumption()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            t.Root.Subsume(t.Num.Of(1L));

            t.Root.Delete(t.Num.Of(1L));
            Assert.False(t.Root.Check(t.Num.Of(1L)));
            t.Root.Assume(t.Num.Of(1L));

            Assert.Contains(t.Root.Rows(t.Num), row => row.Subsumed == false);
        }

        [Fact]
        public void AKeyWithNoRowDeletesNothing()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            t.Root.Assume(f.Of(1L).Is(10L));
            var version = t.Root.Version;

            t.Root.Delete(f.Of(2L));
            t.Root.Delete(t.Num.Of(1L));

            Assert.Equal(version, t.Root.Version);
            Assert.Equal(10L, t.Root.Get(f.Of(1L)));
        }

        /// <summary>
        /// A deleted constructor term's row goes, and its class stays with its other members and the rows that name it;
        /// making the term again mints a new class, as egglog's get-or-add does.
        /// </summary>
        [Fact]
        public void DeletingATermKeepsItsClass()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            t.Root.Union(a, t.Root.Make(t.Lit.Of(1L)));
            t.Root.Assume(t.Tag.Of(a));

            t.Root.Delete(t.A.Of());

            Assert.Equal(0, t.Root.CountRows(t.A));
            Assert.True(t.Root.AreEqual(a, t.Lit.Of(1L)));
            Assert.True(t.Root.Check(t.Tag.Of(a)));
            var again = t.Root.Make(t.A.Of());
            Assert.False(t.Root.AreEqual(again, t.Lit.Of(1L)));
        }

        /// <summary>
        /// The keys are evaluated as egglog's top-level action evaluates them: a missing constructor term is made, and
        /// nothing is deleted, since no row can have a new class for a key.
        /// </summary>
        [Fact]
        public void AHostDeleteMakesTheMissingTermsOfItsKeys()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, long>("f");

            t.Root.Delete(f.Of(t.Lit.Of(7L)));

            Assert.True(t.Root.Check(Fact.Exists(t.Lit.Of(7L))));
            Assert.Equal(0, t.Root.CountRows(f));
        }

        /// <summary>
        /// A rule deletes the rows it matches; deleting is no change, so a rule that only deletes saturates after the
        /// iteration that deletes, as in egglog.
        /// </summary>
        [Fact]
        public void ARuleDeletesAndADeletionIsNoChange()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("drop big", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Greater.Of(x, 2L));
                r.Delete(t.Num.Of(x));
            });
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L), t.Num.Of(4L));

            var report = t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(1, report.Iterations);
            Assert.False(report.Changed);
            Assert.Equal(2, t.Root.CountRows(t.Num));
            Assert.False(t.Root.Check(t.Num.Of(3L)));
            Assert.Contains("(delete (num x))", t.Rules.Rules[0].ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// An iteration's deletions land before its writes, so a key one rule deletes and another writes keeps the
        /// written row, and the merge does not see the deleted value: a sum gives the new value, not the two added.
        /// </summary>
        [Fact]
        public void AnIterationDeletesBeforeItWrites()
        {
            var t = new NodeTheory();
            var total = t.Graph.DefineFunction<string, long>("total", Sum);
            t.Rules.DefineRule("reset", r => r.Delete(total.Of("k")));
            t.Rules.DefineRule("set", r => r.Then(total.Of("k").Is(3L)));
            t.Root.Assume(total.Of("k").Is(5L));

            t.Root.Run(t.Rules);

            Assert.Equal(3L, t.Root.Get(total.Of("k")));
        }

        /// <summary>
        /// A rule that deletes a row and writes it back settles, as in egglog: a fact the table already holds stages
        /// nothing, so the delete wins and the next iteration finds nothing to match. In a child of a frozen root too.
        /// </summary>
        [Fact]
        public void ARuleThatDeletesAndWritesBackARowSettles()
        {
            var root = Rewriting();
            var report = root.Root.Run(Schedule.Saturate(root.Rules));
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.Equal(0, root.Root.CountRows(root.Num));

            var t = Rewriting();
            t.Root.Freeze();
            var child = t.Root.CreateChild();
            report = child.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.Equal(0, child.CountRows(t.Num));
            Assert.Equal(2, t.Root.CountRows(t.Num));

            static NodeTheory Rewriting()
            {
                var t = new NodeTheory();
                t.Rules.DefineRule("rewrite", r =>
                {
                    var n = r.Var<long>("n");
                    r.When(t.Num.Of(n));
                    r.Delete(t.Num.Of(n));
                    r.Then(t.Num.Of(n));
                });
                t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L));
                return t;
            }
        }

        /// <summary>
        /// One rule deletes a row the table holds and another writes it in the same iteration: the write stages nothing,
        /// so the row goes, as in egglog. A row the table lacks is written and survives a delete of it.
        /// </summary>
        [Fact]
        public void AWriteOfAHeldRowDoesNotOutliveADeleteOfIt()
        {
            var t = new NodeTheory();
            var keep = t.Graph.DefineRelation<long>("keep");
            t.Rules.DefineRule("drop", r =>
            {
                var n = r.Var<long>("n");
                r.When(keep.Of(n));
                r.Delete(t.Num.Of(n));
            });
            t.Rules.DefineRule("add", r =>
            {
                var n = r.Var<long>("n");
                r.When(keep.Of(n));
                r.Then(t.Num.Of(n));
            });
            t.Rules.DefineRule("fresh", r =>
            {
                var n = r.Var<long>("n");
                r.When(keep.Of(n));
                r.Then(t.Num.Of(Primitives.Add.Of(n, 10L)));
                r.Delete(t.Num.Of(Primitives.Add.Of(n, 10L)));
            });
            t.Root.Assume(keep.Of(1L), t.Num.Of(1L));

            t.Root.Run(t.Rules);

            Assert.False(t.Root.Check(t.Num.Of(1L)));
            Assert.True(t.Root.Check(t.Num.Of(11L)));
        }

        [Fact]
        public void AScopeWithALiveChildCannotDelete()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("drop", r => r.Delete(t.Num.Of(1L)));
            t.Root.Assume(t.Num.Of(1L));
            var child = t.Root.CreateChild();
            var version = t.Root.Version;

            var host = Assert.Throws<InvalidOperationException>(() => t.Root.Delete(t.Num.Of(1L)));
            var run = Assert.Throws<InvalidOperationException>(() => t.Root.Run(t.Rules));

            Assert.Contains("live descendant", host.Message, StringComparison.Ordinal);
            Assert.Contains("Rule 'drop' deletes rows of 'num'", run.Message, StringComparison.Ordinal);
            Assert.Equal(version, t.Root.Version);
            Assert.False(t.Root.IsFaulted);
            Assert.True(child.Check(t.Num.Of(1L)));
        }

        /// <summary>
        /// A child deletes only under a frozen parent: the row is hidden in the child and its later descendants, and the
        /// parent keeps it. Under a writable parent the child is refused, and so is a run of a rule that deletes.
        /// </summary>
        [Fact]
        public void AChildDeletesOnlyUnderAFrozenParent()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("drop", r => r.Delete(t.Num.Of(2L)));
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L));
            var writable = t.Root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => writable.Delete(t.Num.Of(1L)));
            var refused = Assert.Throws<InvalidOperationException>(() => writable.Run(t.Rules));
            Assert.Contains("its parent is writable", refused.Message, StringComparison.Ordinal);
            Assert.False(writable.IsFaulted);
            writable.Dispose();

            t.Root.Freeze();
            var child = t.Root.CreateChild();
            child.Delete(t.Num.Of(1L));
            child.Run(t.Rules);
            var grandchild = child.CreateChild();

            Assert.Equal(0, child.CountRows(t.Num));
            Assert.Equal(0, grandchild.CountRows(t.Num));
            Assert.Equal(2, t.Root.CountRows(t.Num));
        }

        [Fact]
        public void ARunThatSplitsCannotDelete()
        {
            var t = new NodeTheory();
            var choice = t.Graph.DefineFunction<string, Set<long>>("choice", ValueLattice.Intersect<long>());
            var split = t.Graph.DefineSplit("pick", s =>
            {
                var (k, d) = s.Vars<string, Set<long>>("k", "d");
                s.When(d.Is(choice.Of(k)));
                s.Where(Primitives.Greater.Of(Primitives.Count<long>().Of(d), 1L));
                var v = s.Cases("v", d);
                s.Assume(choice.Of(k).Is(Primitives.Singleton<long>().Of(v)));
            });
            t.Rules.DefineRule("drop", r => r.Delete(t.Num.Of(1L)));
            t.Root.Assume(t.Num.Of(1L), choice.Of("k").Is(Set.Of(1L, 2L)));

            var e = Assert.Throws<InvalidOperationException>(() => t.Root.Run(Schedule.Sequence(t.Rules, Schedule.Split(split, t.Rules))));

            Assert.Contains("the schedule splits", e.Message, StringComparison.Ordinal);
            Assert.True(t.Root.Check(t.Num.Of(1L)));
        }

        /// <summary>
        /// Only the root writes a function whose merge is not a lattice, so a child cannot delete from one either: the
        /// deletion would hide the row in a layer of the child.
        /// </summary>
        [Fact]
        public void AChildCannotDeleteFromAFunctionWithAMerge()
        {
            var t = new NodeTheory();
            var total = t.Graph.DefineFunction<string, long>("total", Sum);
            t.Rules.DefineRule("reset", r => r.Delete(total.Of("k")));
            t.Root.Assume(total.Of("k").Is(5L));
            t.Root.Freeze();
            var child = t.Root.CreateChild();

            var host = Assert.Throws<InvalidOperationException>(() => child.Delete(total.Of("k")));
            var run = Assert.Throws<InvalidOperationException>(() => child.Run(t.Rules));

            Assert.Contains("only the root may write it", host.Message, StringComparison.Ordinal);
            Assert.Contains("only the root may run it", run.Message, StringComparison.Ordinal);
            Assert.Equal(5L, child.Get(total.Of("k")));
        }

        /// <summary>
        /// A scoped read of the scope's own tag reads the scope's own table through a mirror, which a deletion keeps
        /// equal to the table: a host deletion deletes the mirror's row in the same batch, and a rule's after the
        /// iteration.
        /// </summary>
        [Fact]
        public void ADeletionKeepsTheScopesOwnMirrorEqualToTheTable()
        {
            var t = new NodeTheory();
            var seen = t.Graph.DefineQuery("seen", q => q.When(t.Num.In(t.Graph.RootTag).Of(q.Var<long>("x"))));
            t.Rules.DefineRule("drop two", r => r.Delete(t.Num.Of(2L)));
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L));
            Assert.Equal(3, t.Root.Count(seen));

            t.Root.Delete(t.Num.Of(1L));
            Assert.Equal(2, t.Root.Count(seen));

            t.Root.Run(t.Rules);
            Assert.Equal(1, t.Root.Count(seen));
            Assert.Equal(1, t.Root.CountRows(t.Num));
        }

        /// <summary>
        /// A deletion that merged in an iteration a later rule's failure stopped keeps the scope's own mirror equal to the
        /// table, as after an iteration that did not fail.
        /// </summary>
        [Fact]
        public void AFailedRunsDeletionKeepsTheScopesOwnMirrorEqualToTheTable()
        {
            var t = new NodeTheory();
            var output = t.Graph.DefineRelation<long>("output");
            var seen = t.Graph.DefineQuery("seen", q => q.When(t.Num.In(t.Graph.RootTag).Of(q.Var<long>("x"))));
            t.Rules.DefineRule("drop two", r => r.Delete(t.Num.Of(2L)));
            t.Rules.DefineRule("fails on three", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(output.Of(Primitives.Divide.Of(10L, Primitives.Subtract.Of(x, 3L))));
            });
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L));
            Assert.Equal(3, t.Root.Count(seen));

            Assert.Throws<RuleEvaluationException>(() => t.Root.Run(t.Rules));
            Assert.Equal(2, t.Root.CountRows(t.Num));
            Assert.Equal(2, t.Root.Count(seen));
        }


        /// <summary>
        /// egglog's update idiom: a rule deletes a row and writes its key again in one iteration. The scope's own mirror
        /// then holds the old value under a key the table still has, so the reconcile compares whole rows: the mirror's
        /// row goes, the next sync copies the new one, and no merge of the two faults the scope.
        /// </summary>
        [Fact]
        public void ADeletionAndAWriteOfOneKeyKeepTheScopesOwnMirrorEqualToTheTable()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var go = t.Graph.DefineRelation<long>("go");
            var one = t.Graph.DefineQuery("one", q => q.When(f.In(t.Graph.RootTag).Of(1L).Is(1L)));
            var two = t.Graph.DefineQuery("two", q => q.When(f.In(t.Graph.RootTag).Of(1L).Is(2L)));
            t.Rules.DefineRule("step", r =>
            {
                var (x, v) = r.Vars<long, long>("x", "v");
                r.When(go.Of(x), v.Is(f.Of(x)));
                r.Delete(go.Of(x));
                r.Delete(f.Of(x));
                r.Then(f.Of(x).Is(Primitives.Add.Of(v, 1L)));
            });
            t.Root.Assume(f.Of(1L).Is(1L), go.Of(1L));
            Assert.Equal(1, t.Root.Count(one));

            t.Root.Run(t.Rules);

            Assert.False(t.Root.IsFaulted);
            Assert.Equal(2L, t.Root.Get(f.Of(1L)));
            Assert.Equal(0, t.Root.Count(one));
            Assert.Equal(1, t.Root.Count(two));
        }

        /// <summary>
        /// A child carrying a tag reads its own table through that tag's mirror too, and its deletions keep the mirror
        /// equal to it, while the frozen parent keeps its rows.
        /// </summary>
        [Fact]
        public void ADeletionInATaggedChildKeepsItsMirrorEqualToItsTable()
        {
            var t = new NodeTheory();
            var outer = t.Graph.DefineScopeTag("outer");
            var seen = t.Graph.DefineQuery("seen", q => q.When(t.Num.In(outer).Of(q.Var<long>("x"))));
            t.Rules.DefineRule("drop two", r => r.Delete(t.Num.Of(2L)));
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L));
            t.Root.Freeze();
            var child = t.Root.CreateChild(outer);
            Assert.Equal(3, child.Count(seen));

            child.Delete(t.Num.Of(1L));
            Assert.Equal(2, child.Count(seen));
            child.Run(t.Rules);

            Assert.Equal(1, child.Count(seen));
            Assert.Equal(3, t.Root.CountRows(t.Num));
        }

        /// <summary>
        /// A rule that inherits its progress into a child does not derive again what a deletion removed before the
        /// child's first run, as egglog's semi-naive rules after <c>push</c> do not.
        /// </summary>
        [Fact]
        public void AnInheritingRuleDoesNotDeriveAgainWhatADeletionRemoved()
        {
            var t = new NodeTheory();
            var big = t.Graph.DefineRelation<long>("big");
            t.Rules.DefineRule("big", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Greater.Of(x, 4L));
                r.Then(big.Of(x));
            });
            t.Root.Assume(t.Num.Of(5L));
            t.Root.Run(t.Rules);
            t.Root.Delete(big.Of(5L));
            t.Root.Freeze();
            var child = t.Root.CreateChild();

            child.Run(t.Rules);

            Assert.False(child.Check(big.Of(5L)));
        }

        [Fact]
        public void TheContradictionIsNotDeleted()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Graph.Contradiction.Of());

            Assert.Throws<ArgumentException>(() => t.Root.Delete(t.Graph.Contradiction.Of()));
            Assert.Throws<RuleDefinitionException>(() => t.Rules.DefineRule("undo", r => r.Delete(t.Graph.Contradiction.Of())));
            Assert.True(t.Root.Check(t.Graph.Contradiction.Of()));
        }
    }

}
