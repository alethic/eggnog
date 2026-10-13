using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A lattice merge that throws in the catch-up a call makes first (N113): the engine's rebuild records it and goes on,
    /// so the rule layer takes it right after the catch-up and reports it at the call that ran the merge, whatever the
    /// call: a read, a query, an extraction or a write. The scope whose catch-up failed and the scope the call was made on
    /// are faulted with one <see cref="RuleEvaluationException"/>; nothing else is.
    /// </summary>
    public sealed class CatchUpFaultTests
    {

        /// <summary>
        /// A child's read whose catch-up merges its parent's new value into its own and the lattice throws: the read throws
        /// <see cref="RuleEvaluationException"/> naming the lattice, with the delegate's exception inside, and the child is
        /// faulted with it; the parent is not. Later calls of the child report the same cause.
        /// </summary>
        [Fact]
        public void AReadWhoseCatchUpMergeThrowsFaultsTheScope()
        {
            var w = new ThrowingLatticeWorld();
            var child = w.Parent.CreateChild();
            var node = child.Make(w.A.Of());
            child.Assume(w.F.Of("x").Is(1L));
            w.Parent.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            var failure = Assert.Throws<RuleEvaluationException>(() => child.Check(w.Tag.Of(node)));

            Assert.Null(failure.RuleName);
            Assert.Contains("throwing max", failure.Message);
            Assert.Equal("merge refused", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
            Assert.True(child.IsFaulted);
            Assert.Same(failure, child.Failure);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => child.Check(w.Tag.Of(node))).InnerException);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => child.Assume(w.Tag.Of(node))).InnerException);
            Assert.False(w.Parent.IsFaulted);
            Assert.True(w.Parent.Check(w.F.Of("x").Is(2L)));
        }

        /// <summary>
        /// The merge throws in the catch-up of a writable ancestor of the scope called: the ancestor whose layer kept the old
        /// value and the scope called are faulted with the same exception. The ancestor's parent, and a sibling of the
        /// called scope, are not; the sibling stays usable.
        /// </summary>
        [Fact]
        public void AnAncestorsCatchUpFailureFaultsTheAncestorAndTheScopeCalled()
        {
            var w = new ThrowingLatticeWorld();
            var middle = w.Parent.CreateChild();
            var bottom = middle.CreateChild();
            var sibling = middle.CreateChild();
            middle.Assume(w.F.Of("x").Is(1L));
            w.Parent.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            var failure = Assert.Throws<RuleEvaluationException>(() => bottom.CountRows(w.Tag));

            Assert.Same(failure, bottom.Failure);
            Assert.Same(failure, middle.Failure);
            Assert.False(w.Parent.IsFaulted);
            Assert.False(sibling.IsFaulted);
            sibling.Assume(w.Tag.Of(sibling.Make(w.A.Of())));
            Assert.Equal(1, sibling.CountRows(w.Tag));
        }

        /// <summary>
        /// An extractor query of a writable scope catches the scope up (N112): a merge that throws there fails the query with
        /// the scope's fault, and the extractor is refused afterwards as every call of a faulted scope is.
        /// </summary>
        [Fact]
        public void AnExtractorQueryWhoseCatchUpMergeThrowsFaultsTheScope()
        {
            var w = new ThrowingLatticeWorld();
            var child = w.Parent.CreateChild();
            var node = child.Make(w.A.Of());
            child.Assume(w.F.Of("x").Is(1L));
            var extractor = child.CreateExtractor();
            Assert.Equal("(A)", extractor.Extract(node).ToString());
            w.Parent.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            var failure = Assert.Throws<RuleEvaluationException>(() => extractor.TryGetCost(node, out _));

            Assert.Same(failure, child.Failure);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => extractor.Extract(node)).InnerException);
        }

        /// <summary>
        /// A query whose entry's catch-up merge throws: unlike a failure of the query's own primitives or filters (D49), the
        /// catch-up merged into the scope's layer, so the query throws <see cref="RuleEvaluationException"/> and the scope
        /// is faulted.
        /// </summary>
        [Fact]
        public void AQueryWhoseCatchUpMergeThrowsFaultsTheScope()
        {
            var w = new ThrowingLatticeWorld();
            var query = w.Graph.DefineQuery("tags", q => q.When(w.Tag.Of(q.Var<Node>("n"))));
            var child = w.Parent.CreateChild();
            child.Assume(w.Tag.Of(child.Make(w.A.Of())));
            child.Assume(w.F.Of("x").Is(1L));
            Assert.Equal(1, child.Count(query));
            w.Parent.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            var failure = Assert.Throws<RuleEvaluationException>(() => child.Count(query));

            Assert.Same(failure, child.Failure);
            Assert.False(w.Parent.IsFaulted);
            Assert.Equal(0, w.Parent.Count(query));
        }

        /// <summary>
        /// A write whose catch-up merge throws fails before it writes anything: the scope is faulted with the lattice's
        /// failure at that call, not at a later one.
        /// </summary>
        [Fact]
        public void AWriteWhoseCatchUpMergeThrowsFaultsTheScopeBeforeWriting()
        {
            var w = new ThrowingLatticeWorld();
            var child = w.Parent.CreateChild();
            child.Assume(w.F.Of("x").Is(1L));
            w.Parent.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            var failure = Assert.Throws<RuleEvaluationException>(() => child.Assume(w.F.Of("y").Is(5L)));

            Assert.Contains("throwing max", failure.Message);
            Assert.Same(failure, child.Failure);
            w.Disarm();
            Assert.False(w.Parent.IsFaulted);
            Assert.False(w.Parent.Check(w.F.Of("y").Is(5L)));
        }

        /// <summary>
        /// Without a throwing merge nothing changes: the catch-up merges the parent's value into the child's (the lattice's
        /// max) and the child is not faulted.
        /// </summary>
        [Fact]
        public void ACatchUpMergeThatSucceedsFaultsNothing()
        {
            var w = new ThrowingLatticeWorld();
            var child = w.Parent.CreateChild();
            child.Assume(w.F.Of("x").Is(1L));
            w.Parent.Assume(w.F.Of("x").Is(2L));

            Assert.True(child.Check(w.F.Of("x").Is(2L)));
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// A lift over cases whose merge of their parent's new value throws (the lattice refuses only a merge that raises the
        /// value, so the lift's own merge into the parent succeeds). The engine's lift catches each case up before it reads
        /// it, and that is where the merge throws: the engine records it in the case, which keeps its old value, and the lift
        /// reads that value (N110). The found-vacuous check after the lift takes the record and faults each case (N113,
        /// N114); the lift completes and reports, and the parent is not faulted and keeps its value.
        /// </summary>
        [Fact]
        public void ALiftGoesOnWhenACasesCatchUpMergeThrows()
        {
            var w = new ThrowingLatticeWorld((old, incoming) => old < incoming);
            using var cases = w.Parent.OpenCases();
            var first = cases.Add();
            var second = cases.Add();
            first.Assume(w.F.Of("x").Is(1L));
            second.Assume(w.F.Of("x").Is(1L));
            w.Parent.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            var report = cases.Lift();

            Assert.False(report.Changed);
            Assert.False(w.Parent.IsFaulted);
            Assert.True(w.Parent.Check(w.F.Of("x").Is(2L)));
            foreach (var @case in new[] { first, second })
            {
                var failure = Assert.IsType<RuleEvaluationException>(@case.Failure);
                Assert.Contains("throwing max", failure.Message);
                Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => @case.Check(w.F.Of("x").Is(1L))).InnerException);
            }

            Assert.Equal(2, cases.Cases.Count);
        }

    }

}
