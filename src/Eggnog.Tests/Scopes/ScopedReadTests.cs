using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Scoped reads through mirror tables: a mirror equals the tagged scope's own view, is synced just before it is
    /// read, and host reads of scoped facts read the tagged scope directly. Scoped views are read-only, and a query
    /// that syncs a mirror moves neither the tagged scope's version nor an extractor: mirror tables are internal, and
    /// extraction does not read them.
    /// </summary>
    public sealed class ScopedReadTests
    {

        [Fact]
        public void AMirrorEqualsTheTaggedScopesView()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            t.Root.Assume(t.Fact.Of("root"), t.Best.Of("k").Is(1L));
            t.Root.Freeze();
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("outer"), t.Best.Of("k").Is(5L), t.Best.Of("j").Is(2L));
            outer.Freeze();
            var inner = outer.CreateChild(t.Inner);
            inner.Assume(t.Fact.Of("inner"), t.Best.Of("k").Is(9L));

            inner.Run(Schedule.Saturate(t.Rules));

            // The mirror is the tagged scope's view: the root's and its own facts, and its lattice values, not the
            // inner scope's.
            Assert.Equal(Rendered(outer, t.Fact), Rendered(inner, copies.Facts).Select(r => r.Replace("outer facts", "fact", StringComparison.Ordinal)));
            Assert.Equal(new[] { "outer", "root" }, inner.Rows(copies.Facts).Select(r => (string)r.Keys[0]));
            Assert.Equal(5L, inner.Get(copies.Bests.Of("k")));
            Assert.Equal(2L, inner.Get(copies.Bests.Of("j")));
            Assert.Equal(9L, inner.Get(t.Best.Of("k")));

            // The mirror table itself, read in the tagged scope, holds the same rows.
            var mirror = t.Graph.Mirrors.Get(t.Fact, t.Outer);
            Assert.Equal("fact@outer", mirror.Name);
            Assert.DoesNotContain(mirror.Handle, t.Graph.Tables);
            Assert.Equal(2, outer.Engine.TableSize(mirror.Function));
            Assert.Equal(outer.CountRows(t.Fact), outer.Engine.TableSize(mirror.Function));
            Assert.Equal(outer.CountRows(t.Best), outer.Engine.TableSize(t.Graph.Mirrors.Get(t.Best, t.Outer).Function));
        }

        [Fact]
        public void AWritableTaggedAncestorIsSyncedBeforeADescendantsRunAndQuery()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var count = t.Graph.DefineQuery("outer facts", q =>
            {
                var x = q.Var<string>("x");
                q.When(t.Fact.In(t.Outer).Of(x));
            });
            var outer = t.Root.CreateChild(t.Outer);
            var inner = outer.CreateChild(t.Inner);
            outer.Assume(t.Fact.Of("a"));

            // A host write into the writable tagged ancestor, then a run and a query in the descendant.
            var steps = inner.Run(Schedule.Saturate(t.Rules), new RunOptions { RecordSteps = true });
            Assert.True(inner.Check(copies.Facts.Of("a")));
            Assert.Contains(steps.Steps, s => s.Kind == RunStepKind.MirrorSync && ReferenceEquals(s.Scope, outer));
            Assert.Equal(1, inner.Count(count));
            Assert.Equal(1, outer.Count(count));

            outer.Assume(t.Fact.Of("b"));
            Assert.Equal(2, inner.Count(count));
            Assert.Equal(2, outer.Count(count));
            inner.Run(Schedule.Saturate(t.Rules));
            Assert.True(inner.Check(copies.Facts.Of("b")));
        }

        [Fact]
        public void AScopedReadWithinTheTaggedScopeSeesItsOwnFactsWithoutLag()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));

            // One iteration: the mirror is synced just before it, so the scoped read sees the scope's own fact.
            var report = outer.Run(Schedule.Repeat(1, t.Rules));

            Assert.Equal(1, report.Iterations);
            Assert.True(outer.Check(copies.Facts.Of("a")));

            // A fact derived in one iteration is seen by the scoped read in the next.
            t.Rules.DefineRule("derive", r =>
            {
                var x = r.Var<string>("x");
                r.When(copies.Facts.Of(x));
                r.Where(x, x => x.Length == 1);
                r.Then(t.Fact.Of(Compute.Of(x, v => v + v)));
            });
            outer.Run(Schedule.Repeat(1, t.Rules));
            Assert.True(outer.Check(t.Fact.Of("aa")));
            Assert.False(outer.Check(copies.Facts.Of("aa")));
            outer.Run(Schedule.Repeat(1, t.Rules));
            Assert.True(outer.Check(copies.Facts.Of("aa")));
        }

        [Fact]
        public void AFrozenTaggedScopeIsNeverSynced()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            t.Root.Freeze();
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));
            var freezeVersion = outer.Version;
            outer.Freeze();

            // The freeze synced the mirror once; the mirror tables are internal, so that left the version where it was.
            // Queries and runs below never sync again.
            Assert.Equal(freezeVersion, outer.Version);
            var frozenVersion = outer.Version;
            var inner = outer.CreateChild(t.Inner);
            var report = inner.Run(Schedule.Saturate(t.Rules), new RunOptions { RecordSteps = true });

            Assert.DoesNotContain(report.Steps, s => s.Kind == RunStepKind.MirrorSync);
            Assert.True(inner.Check(copies.Facts.Of("a")));
            Assert.Equal(frozenVersion, outer.Version);
        }

        [Fact]
        public void HostReadsOfScopedFactsReadTheResolvedScopeDirectly()
        {
            var t = new ScopeTheory();
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"), t.Best.Of("k").Is(3L));
            var inner = outer.CreateChild(t.Inner);
            inner.Assume(t.Fact.Of("b"), t.Best.Of("k").Is(7L));

            // No rule reads fact or best scoped, so there is no mirror: the reads go to the outer scope itself.
            Assert.Empty(t.Graph.Mirrors.AllCopyRules());
            Assert.True(inner.Check(t.Fact.In(t.Outer).Of("a")));
            Assert.False(inner.Check(t.Fact.In(t.Outer).Of("b")));
            Assert.True(inner.Check(Fact.In(t.Outer, t.Fact.Of("a"), t.Best.Of("k").Is(3L))));
            Assert.Equal(3L, inner.Get(t.Best.In(t.Outer).Of("k")));
            Assert.Equal(7L, inner.Get(t.Best.Of("k")));
            Assert.Equal(new[] { "a" }, inner.Rows(t.Fact.In(t.Outer)).Select(r => (string)r.Keys[0]));
            Assert.Equal(1, inner.CountRows(t.Fact.In(t.Outer)));
            Assert.Equal(2, inner.CountRows(t.Fact));
            Assert.Throws<InvalidOperationException>(() => outer.Check(t.Fact.In(t.Inner).Of("b")));
        }

        [Fact]
        public void ScopedViewsAreReadOnly()
        {
            var t = new ScopeTheory();
            var outer = t.Root.CreateChild(t.Outer);
            Assert.Contains("read-only", Assert.Throws<ArgumentException>(() => outer.Assume(t.Fact.In(t.Outer).Of("a"))).Message);
            Assert.Contains("read-only", Assert.Throws<ArgumentException>(() => outer.Assume(t.Best.In(t.Outer).Of("k").Is(1L))).Message);
            Assert.Contains("read-only", Assert.Throws<ArgumentException>(() => outer.Assume(Fact.In(t.Outer, t.Fact.Of("a")))).Message);
            Assert.False(outer.Check(t.Fact.Of("a")));
            Assert.Equal("fact@outer", t.Fact.In(t.Outer).ToString());
            Assert.Same(t.Outer, t.Fact.In(t.Outer).ReadScope);
            Assert.Null(t.Fact.ReadScope);
        }

        /// <summary>
        /// The rows of a table in a scope, rendered.
        /// </summary>
        static string[] Rendered(Scope scope, TableHandle table) => scope.Rows(table).Select(r => r.ToString()).ToArray();

        /// <summary>
        /// A query writes nothing, so it never moves <see cref="Scope.Version"/>; a query whose scoped atoms read a
        /// writable tagged scope syncs that scope's mirror first, and that sync does not move it either.
        /// </summary>
        [Fact]
        public void AScopedQueryOnAWritableTaggedScopeLeavesItsVersion()
        {
            var t = new ScopeTheory();
            var query = t.Graph.DefineQuery("scoped", q => q.When(t.Fact.In(t.Outer).Of(q.Var<string>("x"))));
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));
            var version = outer.Version;

            Assert.True(outer.Any(query));

            Assert.Equal(version, outer.Version);
        }

        /// <summary>
        /// Queries never invalidate an extractor, scoped queries included.
        /// </summary>
        [Fact]
        public void AScopedQueryOnAWritableTaggedScopeLeavesItsExtractors()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var leaf = graph.DefineConstructor<long, Node>("Leaf");
            var fact = graph.DefineRelation<string>("fact");
            var outerTag = graph.DefineScopeTag("outer");
            var query = graph.DefineQuery("scoped", q => q.When(fact.In(outerTag).Of(q.Var<string>("x"))));
            var outer = graph.Root.CreateChild(outerTag);
            var node = outer.Make(leaf.Of(1L));
            outer.Assume(fact.Of("a"));
            var extractor = outer.CreateExtractor();

            Assert.Equal(1, outer.Count(query));

            Assert.Equal("(Leaf 1)", extractor.Extract(node).ToString());
        }

        /// <summary>
        /// A write through a scoped view, or through <c>Fact.In</c>, is an <see cref="ArgumentException"/> in a host
        /// write, and nothing is written.
        /// </summary>
        [Fact]
        public void HostWritesThroughScopedViewsAreArgumentExceptions()
        {
            var t = new ScopeTheory();
            var outer = t.Root.CreateChild(t.Outer);
            var version = outer.Version;

            Assert.Throws<ArgumentException>(() => outer.Assume(t.Fact.Of("b"), t.Fact.In(t.Outer).Of("a")));
            Assert.Throws<ArgumentException>(() => outer.Assume(t.Fact.Of("b"), Fact.In(t.Outer, t.Fact.Of("a"))));
            Assert.Throws<ArgumentException>(() => outer.Assume(t.Best.In(t.Outer).Of("k").Is(1L)));

            Assert.Equal(version, outer.Version);
            Assert.False(outer.Check(t.Fact.Of("b")));
        }

        /// <summary>
        /// A query in a writable scope does not move its Version, and a query syncs the mirrors it reads, which writes
        /// the mirror rows into a writable tagged scope. The mirror tables are internal, so their sync moves neither
        /// the tagged scope's version nor any extractor (extraction does not read them); once synced, a second query
        /// moves nothing.
        /// </summary>
        [Fact]
        public void AScopedQueryInAWritableTaggedScopeMovesNeitherItsVersionNorAnExtractor()
        {
            var t = new ScopeTheory();
            t.Graph.DefineSort<Node>("Node");
            var a = t.Graph.DefineConstructor<Node>("A");
            var query = t.Graph.DefineQuery("outer facts", q => q.When(t.Fact.In(t.Outer).Of(q.Var<string>("x"))));
            var outer = t.Root.CreateChild(t.Outer);
            var term = outer.Make(a.Of());
            outer.Assume(t.Fact.Of("a"));
            var extractor = outer.CreateExtractor();
            var version = outer.Version;

            Assert.Equal(1, outer.Count(query));

            Assert.Equal(version, outer.Version);
            Assert.Equal("(A)", extractor.Extract(term).ToString());

            // Once synced, a second query moves nothing.
            version = outer.Version;
            Assert.Equal(1, outer.Count(query));
            Assert.Equal(version, outer.Version);
        }

        /// <summary>
        /// A nested scope of the same tag: a later write into the writable outer scope reaches the inner scope's view
        /// of the lattice function and the relation, and the inner scope's mirror follows it at its next run.
        /// </summary>
        [Fact]
        public void ANestedScopeOfTheSameTagFollowsItsParentsLaterWrites()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var a = t.Root.CreateChild(t.Outer);
            a.Assume(t.Fact.Of("x"), t.Best.Of("k").Is(5L));
            a.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(5L, a.Get(copies.Bests.Of("k")));

            var b = a.CreateChild(t.Outer);
            b.Assume(t.Best.Of("k").Is(9L));
            b.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(9L, b.Get(copies.Bests.Of("k")));

            a.Assume(t.Fact.Of("y"), t.Best.Of("k").Is(20L));
            Assert.Equal(20L, b.Get(t.Best.Of("k")));
            Assert.Equal(20L, b.Get(t.Best.In(t.Outer).Of("k")));

            b.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(20L, b.Get(copies.Bests.Of("k")));
            Assert.True(b.Check(copies.Facts.Of("y")));
        }

        /// <summary>
        /// A query with a scoped atom on a writable tagged scope keeps the scope's Version, and an extractor created
        /// before the query still extracts after it.
        /// </summary>
        [Fact]
        public void AScopedQueryOnAWritableTaggedScopeKeepsItsVersionAndExtractor()
        {
            var t = new NodeTheory();
            var outerTag = t.Graph.DefineScopeTag("outer");
            var nums = t.Graph.DefineQuery("outer nums", q => q.When(t.Num.In(outerTag).Of(q.Var<long>("n"))));
            var one = t.Root.Make(t.Lit.Of(1L));
            var outer = t.Root.CreateChild(outerTag);
            outer.Assume(t.Num.Of(5L));
            var extractor = outer.CreateExtractor();
            var version = outer.Version;

            Assert.Equal(1, outer.Count(nums));

            Assert.Equal("(Lit 1)", extractor.Extract(one).ToString());
            Assert.Equal(version, outer.Version);
        }

        /// <summary>
        /// The same from a descendant: a query whose scoped atom resolves to the writable tagged parent does not make
        /// the descendant's extractor stale.
        /// </summary>
        [Fact]
        public void AScopedQueryInADescendantKeepsTheDescendantsExtractor()
        {
            var t = new NodeTheory();
            var outerTag = t.Graph.DefineScopeTag("outer");
            var nums = t.Graph.DefineQuery("outer nums", q => q.When(t.Num.In(outerTag).Of(q.Var<long>("n"))));
            var one = t.Root.Make(t.Lit.Of(1L));
            var outer = t.Root.CreateChild(outerTag);
            outer.Assume(t.Num.Of(5L));
            var inner = outer.CreateChild();
            var extractor = inner.CreateExtractor();

            Assert.Equal(1, inner.Count(nums));

            Assert.Equal("(Lit 1)", extractor.Extract(one).ToString());
        }

    }

}
