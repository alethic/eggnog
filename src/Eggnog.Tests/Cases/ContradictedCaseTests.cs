using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Cases
{

    /// <summary>
    /// What a case whose contradiction stopped it answers (D45, §6.6.8): it has no model, so it answers whether it is
    /// contradictory and its identity, does nothing on <c>Assume</c>, returns <c>false</c> from <c>Union</c>, returns at once
    /// from <c>Run</c>, may be disposed, and refuses every other read and write with
    /// <see cref="ScopeContradictedException"/>. Any other contradictory scope stays fully usable.
    /// </summary>
    public sealed class ContradictedCaseTests
    {

        [Fact]
        public void AStoppedCaseAnswersOnlyWhatNeedsNoModel()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            var b = parent.Make(t.B.Of());
            var query = t.Graph.DefineQuery("tags", q => { var n = q.Var<Node>("n"); q.When(t.Tag.Of(n)); });
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            var mine = @case.Make(t.Wrap.Of(a));
            var extractor = @case.CreateExtractor();
            var version = @case.Version;

            // A rule derives the contradiction in the case, which stops it (D11): Run reports it.
            t.Rules.DefineRule("a tagged node contradicts", r => { var n = r.Var<Node>("n"); r.When(t.Tag.Of(n)); r.Then(t.Graph.Contradiction.Of()); });
            @case.Assume(t.Tag.Of(a));
            var stopped = @case.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(RunStopReason.Contradiction, stopped.StopReason);
            Assert.True(@case.Engine.IsStopped);

            // What still answers.
            Assert.True(@case.IsContradictory);
            Assert.True(@case.IsCase);
            Assert.False(@case.IsFaulted);
            Assert.Null(@case.Failure);
            Assert.False(@case.IsDisposed);
            Assert.False(@case.IsFrozen);
            Assert.Same(parent, @case.Parent);
            Assert.Empty(@case.Children);
            Assert.Empty(@case.LocalRules);
            Assert.True(@case.Version > version);
            Assert.StartsWith(parent.Path + "/case#", @case.Path, StringComparison.Ordinal);
            @case.Assume(t.Tag.Of(b));                                       // nothing
            Assert.False(@case.Union(a, b));
            var again = @case.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(RunStopReason.Contradiction, again.StopReason);
            Assert.Equal(0, again.Iterations);

            // What needs a model.
            void Refused(Action call)
            {
                var refused = Assert.Throws<ScopeContradictedException>(call);
                Assert.Equal(@case.Path, refused.ScopePath);
                Assert.IsAssignableFrom<InvalidOperationException>(refused);
            }

            Refused(() => @case.Check(t.Tag.Of(a)));
            Refused(() => @case.Get(t.A.Of()));
            Refused(() => @case.TryGet(t.A.Of(), out _));
            Refused(() => @case.Find(mine));
            Refused(() => @case.AreEqual(a, b));
            Refused(() => @case.AreEqual(a, t.A.Of()));
            Refused(() => @case.Count(query));
            Refused(() => @case.Any(query));
            Refused(() => @case.ForEachMatch(query, _ => true));
            Refused(() => @case.CountRows(t.Tag));
            Refused(() => @case.Rows(t.Tag));
            Refused(() => @case.Extract(a));
            Refused(() => @case.CreateExtractor());
            Refused(() => extractor.Extract(mine));
            Refused(() => extractor.TryGetCost(mine, out _));
            Refused(() => @case.Make(t.B.Of()));
            Refused(() => @case.Write(w => w.Assume(t.Tag.Of(b))));
            Refused(() => @case.CreateChild());
            Refused(() => @case.OpenCases());
            Refused(() => @case.DefineRule(t.Rules, "late", r => { }));
            Refused(@case.Freeze);

            // The parent is untouched, and the lift drops the case as vacuous.
            Assert.False(parent.IsContradictory);
            var lifted = cases.Lift();
            Assert.True(lifted.ParentContradicted);
            Assert.True(@case.IsDisposed);
            Assert.True(parent.IsContradictory);
        }

        [Fact]
        public void AContradictoryScopeThatIsNotACaseStaysUsable()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            parent.Assume(t.Graph.Contradiction.Of());

            Assert.True(parent.IsContradictory);
            Assert.False(parent.IsCase);
            Assert.False(parent.Engine.IsStopped);
            parent.Assume(t.Tag.Of(a));
            Assert.True(parent.Check(t.Tag.Of(a)));
            Assert.Equal("(A)", parent.Extract(a).ToString());
            Assert.NotNull(parent.CreateChild());
        }

        [Fact]
        public void AWriteThatContradictsACaseStopsIt()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var @case = cases.Add();

            @case.Assume(t.Graph.Contradiction.Of());
            Assert.True(@case.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => @case.Check(t.Graph.Contradiction.Of()));

            // A batch that stops the case commits; the case is stopped from then on.
            var other = cases.Add();
            var a = other.Make(t.A.Of());
            other.Write(w => { w.Make(t.Wrap.Of(a)); w.Assume(t.Graph.Contradiction.Of()); });
            Assert.True(other.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => other.Find(a));

            @case.Dispose();
            Assert.True(@case.IsDisposed);
        }

        /// <summary>
        /// N104: a scope under a stopped case has no model either, and the engine refuses to catch it up with the stopped
        /// case (colors §6.9). A plain child, and a case of a set opened under that child, answer as the stopped case does,
        /// naming themselves and the stopped case; the lift that drops the case disposes everything under it.
        /// </summary>
        [Fact]
        public void AScopeUnderAStoppedCaseAnswersAsTheStoppedCase()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            var b = parent.Make(t.B.Of());
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            var child = @case.CreateChild();
            using var inner = child.OpenCases();
            var grandcase = inner.Add();
            var version = child.Version;

            @case.Assume(t.Graph.Contradiction.Of());
            Assert.True(@case.IsContradictory);

            foreach (var below in new[] { child, grandcase })
            {
                Assert.True(below.IsContradictory);
                below.Assume(t.Tag.Of(a));                                    // nothing
                Assert.False(below.Union(a, b));
                var run = below.Run(Schedule.Saturate(t.Rules));
                Assert.Equal(RunStopReason.Contradiction, run.StopReason);
                Assert.Equal(0, run.Iterations);
                Assert.False(below.IsFaulted);

                void Refused(Action call)
                {
                    var refused = Assert.Throws<ScopeContradictedException>(call);
                    Assert.Equal(below.Path, refused.ScopePath);
                    Assert.Contains(@case.Path, refused.Message, StringComparison.Ordinal);
                }

                Refused(() => below.Check(t.Tag.Of(a)));
                Refused(() => below.Find(a));
                Refused(() => below.Extract(a));
                Refused(() => below.Make(t.Wrap.Of(a)));
                Refused(() => below.Write(w => w.Assume(t.Tag.Of(a))));
                Refused(() => below.CreateChild());
                Refused(() => below.OpenCases());
                Refused(() => below.DefineRule(t.Rules, "late", r => { }));
                Refused(below.Freeze);
            }

            Assert.Equal(version, child.Version);
            Assert.Equal(child.Path, Assert.Throws<ScopeContradictedException>(() => inner.Add()).ScopePath);
            Assert.Equal(child.Path, Assert.Throws<ScopeContradictedException>(() => inner.Lift()).ScopePath);

            Assert.True(cases.Lift().ParentContradicted);
            Assert.True(@case.IsDisposed);
            Assert.True(child.IsDisposed);
            Assert.True(grandcase.IsDisposed);
            Assert.Empty(parent.Children);
        }

        /// <summary>
        /// D45 and I25: a case its parent's contradiction reached since its last call is stopped once it catches up, and the
        /// structural calls that enter without reading catch it up before they check (N104): <c>Freeze</c> answers as on
        /// any stopped case.
        /// </summary>
        [Fact]
        public void AFreezeOfACaseContradictedThroughItsParentIsRefusedAsOnAStoppedCase()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            parent.Assume(t.Graph.Contradiction.Of());

            Assert.Equal(@case.Path, Assert.Throws<ScopeContradictedException>(@case.Freeze).ScopePath);
            Assert.True(@case.IsContradictory);
            Assert.False(@case.IsFaulted);
        }

        /// <summary>
        /// D45, N102, N104 and N112: an extractor made before its case was contradicted through the case's parent catches
        /// its scope up before it checks, so it refuses as on a stopped case, whichever extractor is asked first, and never
        /// extracts from a scope with no model. Before the parent's contradiction, the catch-up of an extraction leaves
        /// <see cref="Scope.Version"/> alone and the extractor keeps working.
        /// </summary>
        [Fact]
        public void AnExtractorUnderACaseContradictedThroughItsParentRefusesAsOnAStoppedCase()
        {
            foreach (var childFirst in new[] { false, true })
            {
                var t = new NodeTheory();
                var parent = t.Root.CreateChild();
                var a = parent.Make(t.A.Of());
                using var cases = parent.OpenCases();
                var @case = cases.Add();
                var child = @case.CreateChild();
                var ofCase = @case.CreateExtractor();
                var ofChild = child.CreateExtractor();
                var version = @case.Version;
                var childVersion = child.Version;
                Assert.Equal("(A)", ofCase.Extract(a).ToString());
                Assert.Equal("(A)", ofChild.Extract(a).ToString());
                Assert.True(ofChild.TryGetCost(a, out _));
                Assert.Equal(version, @case.Version);
                Assert.Equal(childVersion, child.Version);

                parent.Assume(t.Graph.Contradiction.Of());

                var order = childFirst ? new[] { (ofChild, child), (ofCase, @case) } : new[] { (ofCase, @case), (ofChild, child) };
                foreach (var (extractor, scope) in order)
                {
                    var refused = Assert.Throws<ScopeContradictedException>(() => extractor.Extract(a));
                    Assert.Equal(scope.Path, refused.ScopePath);
                    Assert.Throws<ScopeContradictedException>(() => extractor.TryGetCost(a, out _));
                    Assert.Throws<ScopeContradictedException>(() => extractor.TryExtract(a, out _));
                }

                Assert.True(@case.IsContradictory);
                Assert.False(@case.IsFaulted);
                Assert.False(child.IsFaulted);
            }
        }

    }

}
