using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// <see cref="Scope.Version"/> and the catch-up a call makes first: a descendant whose classes merged before a
    /// writable ancestor wrote rows over them must copy those rows at its next call, and that catch-up is left out of
    /// its <see cref="Scope.Version"/> and out of every writable ancestor's, whatever the call is: a read, a query, a
    /// write, a run or a freeze. The copies a scope's own union makes over rows already there still move it.
    /// </summary>
    public sealed class CatchUpVersionTests
    {

        /// <summary>
        /// A read in the innermost of two writable children after the root writes: neither child's Version moves,
        /// though both catch up and the inner one copies rows over its merged classes.
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
        /// A write that changes nothing after an ancestor's write: writes that change nothing never move it.
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
        /// A run with nothing to do after an ancestor's write: its catch-up moves no Version, and the run reports no
        /// change.
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
        /// The scope's own union over rows an ancestor already holds: the copies it makes are its own change.
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

        /// <summary>
        /// A query never moves <see cref="Scope.Version"/>, also in a descendant whose ancestor wrote rows the
        /// descendant's union touches, which the catch-up of the query copies.
        /// </summary>
        [Fact]
        public void APlainQueryInADescendantAfterAnAncestorWriteKeepsItsVersion()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var query = t.Graph.DefineQuery("tags", q => q.When(t.Tag.Of(q.Var<Node>("x"))));
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);
            Assert.Equal(0, inner.Count(query));
            var version = inner.Version;

            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));
            Assert.Equal(1, inner.Count(query));
            Assert.Equal(version, inner.Version);
        }

        /// <summary>
        /// The same with a host read, which never moves it either: <c>Check</c> after the ancestor's write catches up
        /// and leaves the version where it was.
        /// </summary>
        [Fact]
        public void AHostReadInADescendantAfterAnAncestorWriteKeepsItsVersion()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);
            Assert.False(inner.Check(t.Tag.Of(a)));
            var version = inner.Version;

            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));
            Assert.Equal(version, inner.Version);
            Assert.True(inner.Check(t.Tag.Of(b)));
            Assert.Equal(version, inner.Version);
        }

        /// <summary>
        /// A batch that changes nothing, after an ancestor's write made before it, keeps the descendant's version.
        /// </summary>
        [Fact]
        public void ABatchThatChangesNothingAfterAnAncestorWriteKeepsVersion()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);
            var version = inner.Version;

            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));
            inner.Write(w => w.Assume(t.Tag.Of(a)));
            Assert.Equal(version, inner.Version);
        }

        /// <summary>
        /// The ancestor's write attempted inside the descendant's batch after the batch's last call is refused, so the
        /// batch's commit catches up with nothing. The batch writes one row of its own, so its Version moves exactly as
        /// far as the same batch moves it when the ancestor wrote before the call: a write into an ancestor never moves
        /// a descendant's Version.
        /// </summary>
        [Fact]
        public void AnAncestorWriteAfterTheLastCallOfADescendantsBatchMovesNoDescendantVersion()
        {
            Assert.Equal(BatchVersionDelta(false), BatchVersionDelta(true));
        }

        /// <summary>
        /// How far a batch of one new row moves a descendant's Version, with the ancestor's write before the batch, or
        /// attempted inside it after the batch's call and refused.
        /// </summary>
        /// <param name="inside">Whether the ancestor's write is attempted inside the batch.</param>
        /// <returns>The distance the batch moved the descendant's Version.</returns>
        static long BatchVersionDelta(bool inside)
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);
            outer.Assume(t.Tag.Of(a));
            Assert.True(inner.Check(t.Tag.Of(b)));
            if (inside == false)
            {
                outer.Assume(t.Tag.Of(b));
                Assert.True(inner.Check(t.Tag.Of(b)));
            }

            var version = inner.Version;
            inner.Write(w =>
            {
                w.Assume(t.Num.Of(1L));
                if (inside)
                    Assert.Throws<InvalidOperationException>(() => outer.Assume(t.Tag.Of(b)));
            });
            Assert.Equal(1, inner.CountRows(t.Tag));
            Assert.True(inner.Check(t.Num.Of(1L)));
            return inner.Version - version;
        }

    }

}
