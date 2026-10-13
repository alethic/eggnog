using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Probes of the R2 fixes (verification round 3): N87 leaves the catch-up a call makes at its entry out of
    /// <see cref="Scope.Version"/>, so a write into a writable ancestor never moves a descendant's Version. These probes
    /// write into the ancestor inside the descendant's own call, after its entry catch-up. N88 refuses that write: the
    /// batch's calls were checked against the ancestor as it stood (D32), so V02 and V03 assert the refusal, and that the
    /// batch then commits only its own changes.
    /// </summary>
    public sealed class R2FixVerification3Tests
    {

        /// <summary>
        /// Control: the ancestor's write made before the descendant's batch, the batch changing nothing.
        /// </summary>
        [Fact]
        public void V01_ABatchThatChangesNothingAfterAnAncestorWriteKeepsVersion()
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
        /// The ancestor's write made from inside the descendant's batch callback (same thread, same domain) is refused with
        /// the rule layer's message (N88); the batch's later calls work, it commits its own row, and the ancestor's write
        /// made after the batch moves no descendant Version (N87).
        /// </summary>
        [Fact]
        public void V02_AnAncestorWriteInsideADescendantsBatchIsRefused()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);

            inner.Write(w =>
            {
                var refused = Assert.Throws<InvalidOperationException>(() => outer.Assume(t.Tag.Of(a), t.Tag.Of(b)));
                Assert.Contains("Write batch open", refused.Message);
                w.Assume(t.Tag.Of(a));
            });
            Assert.True(inner.Check(t.Tag.Of(b)));
            Assert.Equal(1, inner.CountRows(t.Tag));
            Assert.Equal(0, outer.CountRows(t.Tag));

            var version = inner.Version;
            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));
            Assert.Equal(1, inner.CountRows(t.Tag));
            Assert.Equal(version, inner.Version);
        }

        /// <summary>
        /// The ancestor's write attempted inside the descendant's batch after the batch's last call is refused (N88), so the
        /// batch's commit catches up with nothing. The batch writes one row of its own, so its Version moves exactly as far
        /// as the same batch moves it when the ancestor wrote before the call (N87: a write into an ancestor never moves a
        /// descendant's Version).
        /// </summary>
        [Fact]
        public void V03_AnAncestorWriteAfterTheLastCallOfADescendantsBatchMovesNoDescendantVersion()
        {
            Assert.Equal(BatchVersionDelta(false), BatchVersionDelta(true));
        }

        /// <summary>
        /// How far a batch of one new row moves a descendant's Version, with the ancestor's write before the batch, or
        /// attempted inside it after the batch's call and refused.
        /// </summary>
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
