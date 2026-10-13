using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Verification probes of the second R3 fix round (N112): extractor queries of a writable scope now catch the scope up
    /// before their stop check. These probe what that catch-up may change for scopes that are not cases: staleness, the
    /// root, and a catch-up whose merge fails.
    /// </summary>
    public sealed class R3FixVerify2Tests
    {

        /// <summary>
        /// §6.10: reads, queries and writes that change nothing the extractor reads never invalidate it. An ancestor's
        /// write of a relation the extractor does not read, caught up by the extraction itself (N112), leaves the
        /// extractor of a plain writable grandchild usable and its <see cref="Scope.Version"/> unchanged (N87).
        /// </summary>
        [Fact]
        public void AnAncestorWriteTheExtractorDoesNotReadLeavesAPlainChildsExtractorUsable()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);
            var extractor = inner.CreateExtractor();
            var first = extractor.Extract(a).ToString();
            var version = inner.Version;

            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));

            Assert.Equal(first, extractor.Extract(a).ToString());
            Assert.True(extractor.TryGetCost(b, out _));
            Assert.Equal(version, inner.Version);
            Assert.True(inner.Check(t.Tag.Of(b)));
        }

        /// <summary>
        /// §6.10: a write in a writable ancestor that changes what the extractor reads makes it stale, with
        /// <see cref="InvalidOperationException"/> and no fault, whether or not the extraction caught the scope up.
        /// </summary>
        [Fact]
        public void AnAncestorWriteTheExtractorReadsMakesAPlainChildsExtractorStale()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            var extractor = inner.CreateExtractor();
            Assert.Equal("(A)", extractor.Extract(a).ToString());
            var version = inner.Version;

            outer.Make(t.Wrap.Of(a));

            Assert.Throws<InvalidOperationException>(() => extractor.Extract(a));
            Assert.False(inner.IsFaulted);
            Assert.Equal(version, inner.Version);
            Assert.Equal("(A)", inner.CreateExtractor().Extract(a).ToString());
        }

        /// <summary>
        /// D18 and the Extractor docs (N112): when the catch-up an extractor query now makes runs a lattice merge that
        /// throws, the query throws <see cref="RuleEvaluationException"/> and the scope is faulted, as the other entry
        /// points that catch up report it; the engine's own panic never escapes. Observed at f6a4a48: the merge runs and
        /// throws inside the extraction's catch-up, its failure is recorded and then dropped, the extraction returns a
        /// term, the scope is not faulted, and the next write of the scope faults it with the lattice's failure.
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
        /// The same as <see cref="AnExtractionWhoseCatchUpMergeThrowsIsAnEvaluationFailure"/>, for the grandchild of the
        /// scope whose catch-up merge throws: the extraction of the grandchild catches its writable ancestors up first.
        /// The query must report a <see cref="RuleEvaluationException"/>, never the engine's panic.
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
        /// For comparison with the extractor probes: a read of the grandchild (<see cref="Scope.Check"/>) whose ancestor's
        /// catch-up merge throws. Pins what the read entry point reports, which the extractor's catch-up should match.
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
        /// §14.1 of colors: the root pays nothing. An extractor of the root keeps working across root writes the extractor
        /// does not read, and the root's <see cref="Scope.Version"/> moves only with those writes.
        /// </summary>
        [Fact]
        public void TheRootsExtractorIsUnchangedByN112()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var extractor = t.Root.CreateExtractor();
            Assert.Equal("(A)", extractor.Extract(a).ToString());
            var version = t.Root.Version;
            Assert.Equal("(A)", extractor.Extract(a).ToString());
            Assert.Equal(version, t.Root.Version);
            t.Root.Assume(t.Tag.Of(a));
            Assert.Equal("(A)", extractor.Extract(a).ToString());
        }

    }

}
