using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Extraction
{

    /// <summary>
    /// Extraction over a scope's context: a child's extraction sees inherited and own rows and the child's equalities.
    /// A change in a writable ancestor that the extractor reads makes a child's extractor stale; a query in the child,
    /// or an ancestor's write the extractor does not read, does not.
    /// </summary>
    public sealed class ContextExtractionTests
    {

        [Fact]
        public void AChildSeesInheritedAndOwnRowsAndItsOwnEqualities()
        {
            var t = new NodeTheory();
            var wrapped = t.Root.Make(t.Wrap.Of(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(2L))));
            t.Root.Freeze();
            var child = t.Root.CreateChild();

            // Inherited rows: the root's term.
            Assert.Equal("(Wrap (Add (Lit 1) (Lit 2)))", child.Extract(wrapped).ToString());

            // Own rows and an equality made only in the child: Add(1, 2) is A there, and A costs less.
            var a = child.Make(t.A.Of());
            child.Assume(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(2L)).Is(t.A.Of()));
            Assert.Equal("(Wrap (A))", child.Extract(wrapped).ToString());
            Assert.Equal("(A)", child.Extract(a).ToString());
            Assert.Equal(2, child.Extract(wrapped).Cost);

            // The root, and a sibling, still see the root's term only.
            Assert.Equal("(Wrap (Add (Lit 1) (Lit 2)))", t.Root.Extract(wrapped).ToString());
            Assert.Equal("(Wrap (Add (Lit 1) (Lit 2)))", t.Root.CreateChild().Extract(wrapped).ToString());
            Assert.Throws<ArgumentException>(() => t.Root.Extract(a));
        }

        [Fact]
        public void AParentChangeMakesAChildsExtractorStaleAndAQueryDoesNot()
        {
            var t = new NodeTheory();
            var lits = t.Graph.DefineQuery("lits", q => q.When(t.Lit.Of(q.Var<long>("n")).Is(q.Var<Node>("x"))));
            var one = t.Root.Make(t.Lit.Of(1L));
            var child = t.Root.CreateChild();
            var extractor = child.CreateExtractor();
            Assert.Equal("(Lit 1)", extractor.Extract(one).ToString());

            // A query in the child, a query at the root, a run that changes nothing, and writes that hold already keep
            // it.
            Assert.Equal(1, child.Count(lits));
            Assert.Equal(1, t.Root.Count(lits));
            child.Run(t.Rules);
            child.Assume(t.Lit.Of(1L).Is(one));
            Assert.Equal("(Lit 1)", extractor.Extract(one).ToString());

            // A union in the writable parent changes what the child sees.
            var b = t.Root.Make(t.B.Of());
            t.Root.Union(one, b);
            Assert.Contains("changed", Assert.Throws<InvalidOperationException>(() => extractor.Extract(one)).Message);
            Assert.Equal("(B)", child.CreateExtractor().Extract(child.Find(one)).ToString());
        }

        [Fact]
        public void ExtractorsOnAFrozenScopeRunOnManyThreads()
        {
            var t = new NodeTheory();
            var terms = new Node[64];
            for (var i = 0; i < terms.Length; i++)
                terms[i] = t.Root.Make(t.Wrap.Of(t.Add.Of(t.Lit.Of(i), t.Lit.Of(i + 1L))));

            t.Root.Freeze();
            var rendered = new string[8][];
            var threads = Support.DedicatedThreads.For(rendered.Length, rendered.Length, k =>
            {
                var extractor = t.Root.CreateExtractor();
                rendered[k] = new string[terms.Length];
                for (var i = 0; i < terms.Length; i++)
                    rendered[k][i] = extractor.Extract(terms[i]).ToString();
            });

            Assert.Equal(rendered.Length, threads);

            for (var i = 0; i < terms.Length; i++)
                Assert.All(rendered, r => Assert.Equal($"(Wrap (Add (Lit {i}) (Lit {i + 1})))", r[i]));
        }

        /// <summary>
        /// Reads, queries and writes that change nothing the extractor reads never invalidate it: an ancestor's write
        /// of a relation the extractor does not read, caught up by the extraction itself, leaves the extractor of a
        /// plain writable grandchild usable and its <see cref="Scope.Version"/> unchanged.
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
        /// A write in a writable ancestor that changes what the extractor reads makes it stale, with
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

    }

}
