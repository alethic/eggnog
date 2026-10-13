using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Verification probes of the R3 review fixes (N104-N111): scopes under a case that a contradiction reached through its
    /// own parent, and extractors made before a case stopped.
    /// </summary>
    public sealed class R3FixVerifyTests
    {

        /// <summary>
        /// N104 and I25: a case its parent's contradiction reached since its last call stops at its next catch-up; a plain
        /// child of it, and a case of a set opened under that child, reached first, answer as the stopped case, without a
        /// fault and without the engine's own exception.
        /// </summary>
        [Fact]
        public void ScopesUnderACaseContradictedThroughItsParentAnswerAsTheStoppedCase()
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

            parent.Assume(t.Graph.Contradiction.Of());

            // The descendants are asked first: their catch-up is what stops the case.
            Assert.True(grandcase.IsContradictory);
            Assert.True(child.IsContradictory);
            foreach (var below in new[] { child, grandcase })
            {
                var refused = Assert.Throws<ScopeContradictedException>(() => below.Check(t.Tag.Of(a)));
                Assert.Equal(below.Path, refused.ScopePath);
                Assert.Contains(@case.Path, refused.Message, StringComparison.Ordinal);
                below.Assume(t.Tag.Of(a));
                Assert.False(below.Union(a, b));
                var run = below.Run(Schedule.Saturate(t.Rules));
                Assert.Equal(RunStopReason.Contradiction, run.StopReason);
                Assert.Throws<ScopeContradictedException>(() => below.CreateChild());
                Assert.Throws<ScopeContradictedException>(() => below.DefineRule(t.Rules, "late", r => { }));
                Assert.Throws<ScopeContradictedException>(below.Freeze);
                Assert.False(below.IsFaulted);
            }

            Assert.Equal(child.Path, Assert.Throws<ScopeContradictedException>(() => inner.Add()).ScopePath);
            Assert.Equal(child.Path, Assert.Throws<ScopeContradictedException>(() => inner.Lift()).ScopePath);
            Assert.Equal(version, child.Version);
            Assert.True(@case.IsContradictory);
            Assert.False(@case.IsFaulted);
            Assert.False(parent.IsFaulted);

            Assert.True(cases.Lift().ParentContradicted);
            Assert.True(@case.IsDisposed);
            Assert.True(child.IsDisposed);
            Assert.True(grandcase.IsDisposed);
        }

        /// <summary>
        /// N102 and N104: an extractor of a plain child, made before the case above it stopped, refuses as the stopped case
        /// does.
        /// </summary>
        [Fact]
        public void AnExtractorOfAChildMadeBeforeItsCaseStoppedRefuses()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            var child = @case.CreateChild();
            var extractor = child.CreateExtractor();
            Assert.NotNull(extractor.Extract(a));

            @case.Assume(t.Graph.Contradiction.Of());

            var refused = Assert.Throws<ScopeContradictedException>(() => extractor.Extract(a));
            Assert.Equal(child.Path, refused.ScopePath);
            Assert.Throws<ScopeContradictedException>(() => extractor.TryGetCost(a, out _));
        }

        /// <summary>
        /// D45, N102 and N104: an extractor never extracts a term from a scope with no model. When the case above it is
        /// contradicted through its own parent (I25), the extractor either refuses as on a stopped case or reports that the
        /// scope changed; it never returns a term.
        /// </summary>
        [Fact]
        public void AnExtractorUnderACaseContradictedThroughItsParentNeverExtracts()
        {
            var t = new NodeTheory();
            var parent = t.Root.CreateChild();
            var a = parent.Make(t.A.Of());
            using var cases = parent.OpenCases();
            var @case = cases.Add();
            var child = @case.CreateChild();
            var ofCase = @case.CreateExtractor();
            var ofChild = child.CreateExtractor();
            Assert.NotNull(ofCase.Extract(a));
            Assert.NotNull(ofChild.Extract(a));

            parent.Assume(t.Graph.Contradiction.Of());

            foreach (var extractor in new[] { ofCase, ofChild })
            {
                var refused = Record.Exception(() => extractor.Extract(a));
                Assert.True(refused is InvalidOperationException, $"extracted from '{extractor.Scope.Path}', which has no model: {refused}");
            }
        }

    }

}
