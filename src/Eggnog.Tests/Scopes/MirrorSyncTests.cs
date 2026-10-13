using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// When a mirror sync is due (D48, §6.8): whenever the tagged scope's view of a mirrored table changed since its last
    /// sync, including through a write into one of its writable ancestors that leaves its own <see cref="Scope.Version"/>
    /// where it was, and whenever the tag gained a mirror since then.
    /// </summary>
    public sealed class MirrorSyncTests
    {

        [Fact]
        public void AHostWriteIntoAnAncestorOfTheTaggedScopeIsSyncedBeforeTheNextRun()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"));
            outer.Run(Schedule.Saturate(t.Rules));
            Assert.True(outer.Check(copies.Facts.Of("a")));

            // The root is a writable ancestor of the tagged scope: its write changes outer's view of fact, not outer's own
            // layer, so outer's version stays where it was.
            var version = outer.Version;
            t.Root.Assume(t.Fact.Of("b"));
            Assert.Equal(version, outer.Version);
            Assert.True(outer.Check(t.Fact.Of("b")));

            var report = outer.Run(Schedule.Saturate(t.Rules), new RunOptions { RecordSteps = true });

            Assert.Contains(report.Steps, s => s.Kind == RunStepKind.MirrorSync && ReferenceEquals(s.Scope, outer));
            Assert.True(outer.Check(copies.Facts.Of("b")));
        }

        [Fact]
        public void AHostWriteIntoAnAncestorOfTheTaggedScopeIsSyncedBeforeTheNextQuery()
        {
            var t = new ScopeTheory();
            var count = t.Graph.DefineQuery("outer facts", q => q.When(t.Fact.In(t.Outer).Of(q.Var<string>("x"))));
            var outer = t.Root.CreateChild(t.Outer);
            var inner = outer.CreateChild(t.Inner);
            outer.Assume(t.Fact.Of("a"));
            Assert.Equal(1, inner.Count(count));

            var version = outer.Version;
            t.Root.Assume(t.Fact.Of("b"));
            Assert.Equal(version, outer.Version);

            Assert.Equal(2, inner.Count(count));
            Assert.Equal(2, outer.Count(count));
        }

        [Fact]
        public void AMirrorCreatedAfterTheTagsLastSyncIsSyncedBeforeItsFirstRead()
        {
            var t = new ScopeTheory();
            var facts = t.Graph.DefineRelation<string>("outer facts");
            t.Rules.DefineRule("copy facts", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Outer).Of(x));
                r.Then(facts.Of(x));
            });
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"), t.Best.Of("k").Is(4L));
            outer.Run(Schedule.Saturate(t.Rules));
            Assert.True(outer.Check(facts.Of("a")));
            Assert.Single(t.Graph.Mirrors.CopyRules(t.Outer));

            // A second mirror of the same tag, made after that sync; outer has not changed since.
            var bests = t.Graph.DefineFunction<string, long>("bests", ValueLattice.Max<long>());
            t.Rules.DefineRule("copy bests", r =>
            {
                var (k, v) = r.Vars<string, long>("k", "v");
                r.When(v.Is(t.Best.In(t.Outer).Of(k)));
                r.Then(bests.Of(k).Is(v));
            });
            Assert.Equal(2, t.Graph.Mirrors.CopyRules(t.Outer).Length);

            outer.Run(Schedule.Saturate(t.Rules));

            Assert.True(outer.Check(bests.Of("k").Is(4L)));
        }

        [Fact]
        public void AMirrorCreatedByAQueryAfterTheTagsLastSyncIsSyncedBeforeTheQuery()
        {
            var t = new ScopeTheory();
            var facts = t.Graph.DefineQuery("outer facts", q => q.When(t.Fact.In(t.Outer).Of(q.Var<string>("x"))));
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"), t.Best.Of("k").Is(4L));
            Assert.Equal(1, outer.Count(facts));

            var bests = t.Graph.DefineQuery("outer bests", q =>
            {
                var (k, v) = q.Vars<string, long>("k", "v");
                q.When(v.Is(t.Best.In(t.Outer).Of(k)));
            });

            Assert.Equal(1, outer.Count(bests));
        }

    }

}
