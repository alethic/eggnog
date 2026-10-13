using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Scoped reads through mirror tables (D1, D48, §6.8, invariant M): a mirror equals the tagged scope's own view, is
    /// synced just before it is read, and host reads of scoped facts read the tagged scope directly.
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

            // The mirror is the tagged scope's view: the root's and its own facts, and its lattice values, not the inner
            // scope's.
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

            // The freeze synced the mirror once; the mirror tables are internal, so that left the version where it was (§6.8).
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

    }

}
