using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A lattice merge that throws in the catch-up a call makes first: the engine's rebuild records it and goes on, so
    /// the rule layer takes it right after the catch-up and reports it at the call that ran the merge, whatever the
    /// call: a read, a query, an extraction, a write or a structural call. The scope whose catch-up failed and the
    /// scope the call was made on are faulted with one <see cref="RuleEvaluationException"/>; nothing else is.
    /// </summary>
    public sealed class CatchUpFaultTests
    {

        /// <summary>
        /// A child's read whose catch-up merges its parent's new value into its own and the lattice throws: the read
        /// throws <see cref="RuleEvaluationException"/> naming the lattice, with the delegate's exception inside, and
        /// the child is faulted with it; the parent is not. Later calls of the child report the same cause.
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
        /// The merge throws in the catch-up of a writable ancestor of the scope called: the ancestor whose layer kept
        /// the old value and the scope called are faulted with the same exception. The ancestor's parent, and a sibling
        /// of the called scope, are not; the sibling stays usable.
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
        /// An extractor query of a writable scope catches the scope up: a merge that throws there fails the query with
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
        /// A query whose entry's catch-up merge throws: a failure of the query's own primitives or filters leaves the
        /// scope usable, but the catch-up merged into the scope's layer, so the query throws
        /// <see cref="RuleEvaluationException"/> and the scope is faulted.
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
        /// Without a throwing merge nothing changes: the catch-up merges the parent's value into the child's (the
        /// lattice's max) and the child is not faulted.
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
        /// A lift over cases whose merge of their parent's new value throws (the lattice refuses only a merge that
        /// raises the value, so the lift's own merge into the parent succeeds). The engine's lift catches each case up
        /// before it reads it, and that is where the merge throws: the engine records it in the case, which keeps its
        /// old value, and the lift reads that value. The found-vacuous check after the lift takes the record and faults
        /// each case; the lift completes and reports, and the parent is not faulted and keeps its value.
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

        /// <summary>
        /// An extractor query catches its writable scope up before its stop check. When that catch-up runs a lattice
        /// merge that throws, the query throws <see cref="RuleEvaluationException"/> and the scope is faulted, as at
        /// the other entry points that catch up; the engine's own panic never escapes, and no term is returned.
        /// </summary>
        [Fact]
        public void AnExtractionWhoseCatchUpMergeThrowsIsAnEvaluationFailure()
        {
            var fail = false;
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A");
            var max = new ValueLattice<long>("throwing max", (x, y) =>
            {
                if (fail)
                    throw new InvalidOperationException("merge refused");

                return Math.Max(x, y);
            }, Math.Min);
            var f = graph.DefineFunction<string, long>("f", max);
            graph.Root.Freeze();
            var parent = graph.Root.CreateChild();
            var child = parent.CreateChild();
            var node = child.Make(a.Of());
            child.Assume(f.Of("x").Is(1L));
            var extractor = child.CreateExtractor();
            Assert.Equal("(A)", extractor.Extract(node).ToString());

            parent.Assume(f.Of("x").Is(2L));
            fail = true;

            var thrown = Record.Exception(() => extractor.Extract(node));
            Assert.True(thrown is RuleEvaluationException, $"expected RuleEvaluationException, got {thrown?.ToString() ?? "no exception"}");
            Assert.True(child.IsFaulted);
        }

        /// <summary>
        /// As <see cref="AnExtractionWhoseCatchUpMergeThrowsIsAnEvaluationFailure"/>, for the grandchild of the scope
        /// whose catch-up merge throws: the extraction of the grandchild catches its writable ancestors up first, and
        /// reports a <see cref="RuleEvaluationException"/>, never the engine's panic.
        /// </summary>
        [Fact]
        public void AnExtractionWhoseAncestorsCatchUpMergeThrowsIsAnEvaluationFailure()
        {
            var fail = false;
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A");
            var max = new ValueLattice<long>("throwing max", (x, y) =>
            {
                if (fail)
                    throw new InvalidOperationException("merge refused");

                return Math.Max(x, y);
            }, Math.Min);
            var f = graph.DefineFunction<string, long>("f", max);
            graph.Root.Freeze();
            var top = graph.Root.CreateChild();
            var middle = top.CreateChild();
            var bottom = middle.CreateChild();
            var node = bottom.Make(a.Of());
            middle.Assume(f.Of("x").Is(1L));
            var extractor = bottom.CreateExtractor();
            Assert.Equal("(A)", extractor.Extract(node).ToString());

            top.Assume(f.Of("x").Is(2L));
            fail = true;

            var thrown = Record.Exception(() => extractor.Extract(node));
            Assert.True(thrown is RuleEvaluationException, $"expected RuleEvaluationException, got {thrown?.ToString() ?? "no exception"}");
        }

        /// <summary>
        /// A read of the same grandchild (<see cref="Scope.Check"/>) whose ancestor's catch-up merge throws reports a
        /// <see cref="RuleEvaluationException"/>, as the extractor's catch-up does.
        /// </summary>
        [Fact]
        public void AReadWhoseAncestorsCatchUpMergeThrowsIsAnEvaluationFailure()
        {
            var fail = false;
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var a = graph.DefineConstructor<Node>("A");
            var tag = graph.DefineRelation<Node>("tag");
            var max = new ValueLattice<long>("throwing max", (x, y) =>
            {
                if (fail)
                    throw new InvalidOperationException("merge refused");

                return Math.Max(x, y);
            }, Math.Min);
            var f = graph.DefineFunction<string, long>("f", max);
            graph.Root.Freeze();
            var top = graph.Root.CreateChild();
            var middle = top.CreateChild();
            var bottom = middle.CreateChild();
            var node = bottom.Make(a.Of());
            middle.Assume(f.Of("x").Is(1L));

            top.Assume(f.Of("x").Is(2L));
            fail = true;

            var thrown = Record.Exception(() => bottom.Check(tag.Of(node)));
            Assert.True(thrown is RuleEvaluationException, $"expected RuleEvaluationException, got {thrown?.ToString() ?? "no exception"}");
        }

        /// <summary>
        /// The merge throws in the catch-up of a writable ancestor of the scope read: the exception carries the
        /// lattice's record (its name in the message, the delegate's exception inside), as when the merge runs in the
        /// scope read, and the ancestor and the scope read are faulted with it.
        /// </summary>
        [Fact]
        public void AnAncestorsCatchUpFailureCarriesTheLatticesRecord()
        {
            var w = new World();
            w.Graph.Root.Freeze();
            var top = w.Graph.Root.CreateChild();
            var middle = top.CreateChild();
            var bottom = middle.CreateChild();
            middle.Assume(w.F.Of("x").Is(1L));
            top.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            var failure = Assert.Throws<RuleEvaluationException>(() => bottom.CountRows(w.Tag));

            Assert.Contains("throwing max", failure.Message);
            Assert.Equal("merge refused", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
            Assert.Same(failure, middle.Failure);
            Assert.Same(failure, bottom.Failure);
        }

        /// <summary>
        /// The structural entry points <c>CreateChild</c>, <c>OpenCases</c>, <c>CaseSet.Add</c>, <c>DefineRule</c> and
        /// <c>Freeze</c> each report a catch-up failure as <see cref="RuleEvaluationException"/>, fault the scope, and
        /// leave nothing behind: no child, no case, no rule, and no freeze.
        /// </summary>
        [Fact]
        public void StructuralCallsReportACatchUpFailureAndLeaveNothingBehind()
        {
            for (var call = 0; call < 5; call++)
            {
                var w = new World();
                var rules = w.Graph.DefineRuleset("rules");
                w.Graph.Root.Freeze();
                var parent = w.Graph.Root.CreateChild();
                var child = parent.CreateChild();
                var cases = call == 2 ? child.OpenCases() : null;
                child.Assume(w.F.Of("x").Is(1L));
                parent.Assume(w.F.Of("x").Is(2L));
                w.Arm();

                Action act = call switch
                {
                    0 => () => child.CreateChild(),
                    1 => () => child.OpenCases(),
                    2 => () => cases!.Add(),
                    3 => () => child.DefineRule(rules, "late", r => { r.When(w.Tag.Of(r.Var<Node>("n"))); r.Then(w.Tag.Of(r.Var<Node>("n"))); }),
                    _ => () => child.Freeze(),
                };

                var failure = Assert.Throws<RuleEvaluationException>(act);
                Assert.Contains("throwing max", failure.Message);
                Assert.Same(failure, child.Failure);
                Assert.False(parent.IsFaulted);
                Assert.Empty(child.Children);
                Assert.Empty(child.LocalRules);
                Assert.False(child.IsFrozen);
                if (cases is not null)
                    Assert.Empty(cases.Cases);
            }
        }

        /// <summary>
        /// Where the refused merge of <see cref="ALiftGoesOnWhenACasesCatchUpMergeThrows"/> runs: inside the engine's
        /// lift, whose lifter catches every case up before reading it. The engine records the failure in the case and
        /// goes on, so the parent is not faulted; the found-vacuous check after the lift takes the record and faults
        /// each case.
        /// </summary>
        [Fact]
        public void ACasesCatchUpMergeInsideTheEnginesLiftFaultsTheCaseNotTheParent()
        {
            var w = new World((old, incoming) => old < incoming);
            w.Graph.Root.Freeze();
            var parent = w.Graph.Root.CreateChild();
            using var cases = parent.OpenCases();
            var first = cases.Add();
            var second = cases.Add();
            first.Assume(w.F.Of("x").Is(1L));
            second.Assume(w.F.Of("x").Is(1L));
            parent.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            cases.Lift();

            Assert.True(w.Refused > 0);
            Assert.Equal(w.Refused, w.RefusedInLifter);
            Assert.False(parent.IsFaulted);
            Assert.True(first.IsFaulted);
            Assert.True(second.IsFaulted);
        }

        /// <summary>
        /// <c>Union</c>, <see cref="Scope.Extract"/> and <see cref="CaseSet.Lift"/> catch the scope up at their entry
        /// and report a catch-up failure as <see cref="RuleEvaluationException"/>, faulting the scope called and not
        /// the case under it.
        /// </summary>
        [Fact]
        public void UnionExtractAndLiftReportTheirEntrysCatchUpFailure()
        {
            for (var call = 0; call < 3; call++)
            {
                var w = new World();
                w.Graph.Root.Freeze();
                var parent = w.Graph.Root.CreateChild();
                var child = parent.CreateChild();
                var a = child.Make(w.A.Of());
                using var cases = child.OpenCases();
                var @case = cases.Add();
                child.Assume(w.F.Of("x").Is(1L));
                parent.Assume(w.F.Of("x").Is(2L));
                w.Arm();

                Action act = call switch
                {
                    0 => () => child.Union(a, a),
                    1 => () => child.Extract(a),
                    _ => () => cases.Lift(),
                };

                var failure = Assert.Throws<RuleEvaluationException>(act);
                Assert.Contains("throwing max", failure.Message);
                Assert.Same(failure, child.Failure);
                Assert.False(@case.IsFaulted);
            }
        }

        /// <summary>
        /// <see cref="Scope.IsContradictory"/> catches the scope up as every read does, so a catch-up failure throws
        /// <see cref="RuleEvaluationException"/> and faults the scope.
        /// </summary>
        [Fact]
        public void IsContradictoryReportsItsCatchUpFailure()
        {
            var w = new World();
            w.Graph.Root.Freeze();
            var parent = w.Graph.Root.CreateChild();
            var child = parent.CreateChild();
            child.Assume(w.F.Of("x").Is(1L));
            parent.Assume(w.F.Of("x").Is(2L));
            w.Arm();

            Assert.Throws<RuleEvaluationException>(() => child.IsContradictory);
            Assert.True(child.IsFaulted);
        }

    }

}
