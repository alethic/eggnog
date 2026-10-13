using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// <see cref="Scope.Version"/> and the catch-up a call makes first (N87, D16, D49): a descendant whose classes merged
    /// before a writable ancestor wrote rows over them must copy those rows at its next call, and that catch-up is left
    /// out of its <see cref="Scope.Version"/> and out of every writable ancestor's, whatever the call is. The copies a
    /// scope's own union makes over rows already there still move it.
    /// </summary>
    public sealed class CatchUpVersionTests
    {

        /// <summary>
        /// A read in the innermost of two writable children after the root writes: neither child's Version moves, though
        /// both catch up and the inner one copies rows over its merged classes.
        /// </summary>
        [Fact]
        public void AReadAfterARootWriteMovesNoVersionAlongTheWritableChain()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);
            Assert.False(inner.Check(t.Tag.Of(a)));
            var outerVersion = outer.Version;
            var innerVersion = inner.Version;

            t.Root.Assume(t.Tag.Of(a), t.Tag.Of(b));
            Assert.True(inner.Check(t.Tag.Of(b)));
            Assert.Equal(1, inner.CountRows(t.Tag));
            Assert.Equal(outerVersion, outer.Version);
            Assert.Equal(innerVersion, inner.Version);
        }

        /// <summary>
        /// A write that changes nothing after an ancestor's write (D16: writes that change nothing never move it).
        /// </summary>
        [Fact]
        public void AWriteThatChangesNothingAfterAnAncestorWriteKeepsVersion()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);
            var version = inner.Version;

            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));
            inner.Assume(t.Tag.Of(a));
            inner.Union(a, b);
            Assert.Equal(version, inner.Version);
        }

        /// <summary>
        /// A run with nothing to do after an ancestor's write: its catch-up moves no Version, and the run reports no change.
        /// </summary>
        [Fact]
        public void ARunAfterAnAncestorWriteCountsOnlyItsOwnChanges()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);
            var version = inner.Version;

            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));
            var report = inner.Run(Schedule.Saturate(t.Rules));
            Assert.False(report.Changed);
            Assert.Equal(version, inner.Version);
            Assert.True(inner.Check(t.Tag.Of(b)));
        }

        /// <summary>
        /// A freeze after the parent wrote and froze: the child catches up at its freeze, and its Version stays.
        /// </summary>
        [Fact]
        public void AFreezeAfterTheParentWroteAndFrozeKeepsVersion()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var child = t.Root.CreateChild();
            child.Union(a, b);
            var version = child.Version;

            t.Root.Assume(t.Tag.Of(a), t.Tag.Of(b));
            t.Root.Freeze();
            child.Freeze();
            Assert.Equal(version, child.Version);
            Assert.True(child.Check(t.Tag.Of(b)));
            Assert.Equal(1, child.CountRows(t.Tag));
        }

        /// <summary>
        /// The scope's own union over rows an ancestor already holds: the copies it makes are its own change (D16).
        /// </summary>
        [Fact]
        public void AUnionOverAnAncestorsRowsMovesVersion()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));
            var inner = outer.CreateChild();
            var version = inner.Version;

            inner.Union(a, b);
            Assert.NotEqual(version, inner.Version);
            Assert.Equal(1, inner.CountRows(t.Tag));
        }

    }

}
