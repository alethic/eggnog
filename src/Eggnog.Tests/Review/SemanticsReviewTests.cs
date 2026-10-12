using System;
using Eggnog.Tests.Support;
using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Review probes of the R1a semantics (§6, §7).
    /// </summary>
    public sealed class SemanticsReviewTests
    {

        /// <summary>
        /// §6.3: <c>c1.Is(c2)</c> is "evaluated now; true: dropped". Two class constants already in one class are equal
        /// now, so the rule must compile and match.
        /// </summary>
        [Fact]
        public void ClassConstantsAlreadyEqualAtDefinitionAreNotNeverMatches()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Union(a, b);
            t.Root.Assume(t.Tag.Of(a));
            t.Rules.DefineRule("both constants", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                r.Then(t.Num.Of(1L));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(t.Num.Of(1L)));
        }

        /// <summary>
        /// The variable-free form of the same: <c>a.Is(b)</c> of two class constants that are equal now.
        /// </summary>
        [Fact]
        public void TwoEqualClassConstantsEquatedDirectlyAreDropped()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Union(a, b);
            t.Rules.DefineRule("equal constants", r =>
            {
                r.When(((Term<Node>)a).Is(b));
                r.Then(t.Num.Of(2L));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(t.Num.Of(2L)));
        }

        /// <summary>
        /// A naive rule that re-inserts a row that already exists changes nothing, so Saturate ends (§6.5.1 Change).
        /// </summary>
        [Fact]
        public void ANaiveRuleReinsertingAnExistingRowSaturates()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("always", r =>
            {
                r.Naive();
                r.Then(t.Num.Of(7L));
            });
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { IterationLimit = 50 });
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Saturated);
            Assert.True(report.Iterations <= 3, $"{report.Iterations} iterations");
        }

        /// <summary>
        /// A naive rule writing a lattice value the stored one absorbs changes nothing, so Saturate ends.
        /// </summary>
        [Fact]
        public void ANaiveLatticeWriteThatIsAbsorbedSaturates()
        {
            var t = new NodeTheory();
            var lo = t.Graph.DefineFunction<long, long>("lo", ValueLattice.Max<long>());
            t.Root.Assume(lo.Of(1L).Is(5L));
            t.Rules.DefineRule("lower bound", r =>
            {
                r.Naive();
                r.Then(lo.Of(1L).Is(3L));
            });
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { IterationLimit = 50 });
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Iterations <= 3, $"{report.Iterations} iterations");
            Assert.Equal(5L, t.Root.Get(lo.Of(1L)));
        }

        /// <summary>
        /// D32: a host write never gives a function without a lattice a second value. A host union that merges two keys
        /// holding different values does, at the rebuild.
        /// </summary>
        [Fact]
        public void AHostUnionThatMergesTwoConflictingKeys()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, long>("f");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(f.Of(a).Is(1L), f.Of(b).Is(2L));
            var thrown = Record.Exception(() => t.Root.Union(a, b));
            Assert.False(t.Root.IsFaulted, "the root is faulted by " + thrown?.GetType().Name);
            Assert.IsType<FactConflictException>(thrown);
            Assert.False(t.Root.AreEqual(a, b));
        }

        /// <summary>
        /// The same inside one batch: two keys of one function without a lattice that a union of the batch merges.
        /// </summary>
        [Fact]
        public void ABatchWhoseUnionMergesTwoConflictingKeysIsAFactConflict()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, long>("f");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var thrown = Record.Exception(() => t.Root.Write(w =>
            {
                w.Assume(f.Of(a).Is(1L), f.Of(b).Is(2L));
                w.Union(a, b);
            }));
            Assert.False(t.Root.IsFaulted, "the root is faulted by " + thrown?.GetType().Name);
            Assert.IsType<FactConflictException>(thrown);
        }

        /// <summary>
        /// Schedule.Run with no ruleset is allowed and does nothing (deviation in the report); a Saturate of it ends.
        /// </summary>
        [Fact]
        public void AScheduleWithAnEmptyRunEnds()
        {
            var t = new NodeTheory();
            var report = t.Root.Run(Schedule.Saturate(Schedule.Run()));
            Assert.Equal(0, report.Iterations);
            Assert.Equal(RunStopReason.Completed, report.StopReason);
        }

        /// <summary>
        /// A let whose target feeds a filter declared before it (§6.3 step 4 places steps in rounds).
        /// </summary>
        [Fact]
        public void AFilterOverALetDeclaredAfterItIsPlaced()
        {
            var t = new NodeTheory();
            var hit = t.Graph.DefineRelation<long>("hit");
            t.Root.Assume(t.Num.Of(4L), t.Num.Of(9L));
            t.Rules.DefineRule("plus one above five", r =>
            {
                var (n, m) = r.Vars<long, long>("n", "m");
                r.When(t.Num.Of(n));
                r.Where(Primitives.Greater.Of(m, 5L));
                r.When(m.Is(Primitives.Add.Of(n, 1L)));
                r.Then(hit.Of(m));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(hit.Of(10L)));
            Assert.False(t.Root.Check(hit.Of(5L)));
        }

        /// <summary>
        /// A class constant in a NotEqual step that lost a union is compared canonically (D3, §6.4.5).
        /// </summary>
        [Fact]
        public void AClassConstantInANotEqualIsCanonical()
        {
            var t = new NodeTheory();
            var other = t.Graph.DefineRelation<Node>("other");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(t.Tag.Of(a));
            t.Root.Union(a, b);
            Assert.Equal(a.Id, t.Root.Find(b).Id);
            t.Rules.DefineRule("not b", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.IsNot(b));
                r.Then(other.Of(x));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.Check(other.Of(a)));
        }

        /// <summary>
        /// A ScopeWriter captured and used on another thread while the batch is open writes into the batch from a
        /// thread that does not hold the root's guard (D47: a second thread fails fast).
        /// </summary>
        [Fact]
        public void AScopeWriterUsedFromAnotherThreadIsRefused()
        {
            var t = new NodeTheory();
            Exception? thrown = null;
            t.Root.Write(w =>
            {
                var thread = new System.Threading.Thread(() => thrown = Record.Exception(() => w.Assume(t.Num.Of(3L))));
                thread.Start();
                thread.Join();
            });
            Assert.IsType<InvalidOperationException>(thrown);
        }

    }

}
