using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// Calls into other scopes of a thread domain while one of its scopes has a <see cref="Scope.Write"/> batch open:
    /// the scopes the batch reads through (its writable ancestors) refuse writes and runs, which would change what the
    /// batch's calls were checked against; everything else is allowed, and the batch is brought up to date with it
    /// before each of its calls and before it commits, without moving <see cref="Scope.Version"/>.
    /// </summary>
    public sealed class OpenBatchDomainTests
    {

        [Fact]
        public void AnAncestorWriteThatWouldConflictWithTheBatchIsRefusedAndTheBatchCommits()
        {
            var t = new NodeTheory();
            var weight = t.Graph.DefineFunction<Node, long>("weight");
            var a = t.Root.Make(t.A.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();

            inner.Write(w =>
            {
                w.Assume(weight.Of(a).Is(1L));
                Assert.Throws<InvalidOperationException>(() => outer.Assume(weight.Of(a).Is(2L)));
                Assert.Throws<InvalidOperationException>(() => t.Root.Assume(weight.Of(a).Is(2L)));
            });

            Assert.False(inner.IsFaulted);
            Assert.True(inner.Check(weight.Of(a).Is(1L)));
            Assert.Equal(0, outer.CountRows(weight));
            Assert.Equal(0, t.Root.CountRows(weight));
        }

        [Fact]
        public void AnAncestorsRunAndEveryAncestorWriteAreRefusedWhileTheBatchIsOpen()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();

            inner.Write(w =>
            {
                Assert.Throws<InvalidOperationException>(() => outer.Run(Schedule.Saturate(t.Rules)));
                Assert.Throws<InvalidOperationException>(() => t.Root.Run(Schedule.Saturate(t.Rules)));
                Assert.Throws<InvalidOperationException>(() => outer.Union(a, b));
                Assert.Throws<InvalidOperationException>(() => outer.Make(t.Wrap.Of(a)));
                Assert.Throws<InvalidOperationException>(() => outer.Write(o => o.Assume(t.Num.Of(1L))));
                w.Assume(t.Num.Of(2L));
            });

            Assert.True(inner.Check(t.Num.Of(2L)));
            Assert.False(outer.Check(t.Num.Of(1L)));
            Assert.NotEqual(outer.Find(a), outer.Find(b));
        }

        [Fact]
        public void AncestorsAcceptWritesAgainOnceTheBatchEndsEvenWhenItsCallbackThrew()
        {
            var t = new NodeTheory();
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();

            Assert.Throws<FormatException>(() => inner.Write(w =>
            {
                w.Assume(t.Num.Of(1L));
                throw new FormatException();
            }));
            outer.Assume(t.Num.Of(2L));
            t.Root.Assume(t.Num.Of(3L));

            inner.Write(w => w.Assume(t.Num.Of(4L)));
            outer.Assume(t.Num.Of(5L));

            Assert.False(inner.Check(t.Num.Of(1L)));
            Assert.Equal(4, inner.CountRows(t.Num));
        }

        [Fact]
        public void WritesIntoADescendantAndASiblingInsideTheBatchAreAllowed()
        {
            var t = new NodeTheory();
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            var sibling = outer.CreateChild();
            var below = inner.CreateChild();

            inner.Write(w =>
            {
                w.Assume(t.Num.Of(1L));
                below.Assume(t.Num.Of(2L));
                sibling.Write(s => s.Assume(t.Num.Of(3L)));
                w.Assume(t.Num.Of(4L));
            });

            Assert.True(inner.Check(t.Num.Of(1L)));
            Assert.True(inner.Check(t.Num.Of(4L)));
            Assert.False(inner.Check(t.Num.Of(2L)));
            Assert.True(below.Check(t.Num.Of(1L)));
            Assert.True(below.Check(t.Num.Of(2L)));
            Assert.True(sibling.Check(t.Num.Of(3L)));
            Assert.False(sibling.Check(t.Num.Of(1L)));
        }

        [Fact]
        public void ANestedBatchInADescendantRefusesTheOuterBatchsAncestorsUntilBothEnd()
        {
            var t = new NodeTheory();
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            var below = inner.CreateChild();

            inner.Write(w =>
            {
                below.Write(v =>
                {
                    Assert.Throws<InvalidOperationException>(() => outer.Assume(t.Num.Of(9L)));
                    v.Assume(t.Num.Of(2L));
                });
                Assert.Throws<InvalidOperationException>(() => outer.Assume(t.Num.Of(9L)));
                w.Assume(t.Num.Of(1L));
            });
            outer.Assume(t.Num.Of(3L));

            Assert.Equal(3, below.CountRows(t.Num));
            Assert.Equal(2, inner.CountRows(t.Num));
        }

        [Fact]
        public void AnAncestorsQueryInsideTheBatchLeavesTheBatchUsableAndItsVersionAsWithoutTheQuery()
        {
            Assert.Equal(BatchVersionDelta(false), BatchVersionDelta(true));
        }

        /// <summary>
        /// How far a batch of two new rows moves a descendant's Version, with the tagged ancestor's query (which syncs
        /// its mirrors after a write into it) made before the batch, or between the batch's calls.
        /// </summary>
        static long BatchVersionDelta(bool inside)
        {
            var t = new ScopeTheory();
            var facts = t.Graph.DefineQuery("outer facts", q => q.When(t.Fact.In(t.Outer).Of(q.Var<string>("x"))));
            var outer = t.Root.CreateChild(t.Outer);
            var inner = outer.CreateChild();
            outer.Assume(t.Fact.Of("a"));
            Assert.Equal(1, outer.Count(facts));
            outer.Assume(t.Fact.Of("b"));
            if (inside == false)
                Assert.Equal(2, outer.Count(facts));

            var version = inner.Version;
            inner.Write(w =>
            {
                w.Assume(t.Fact.Of("x"));
                if (inside)
                    Assert.Equal(2, outer.Count(facts));

                w.Assume(t.Fact.Of("y"));
            });

            Assert.Equal(4, inner.CountRows(t.Fact));
            Assert.Equal(2, outer.Count(facts));
            return inner.Version - version;
        }

        /// <summary>
        /// The ancestor's write made from inside the descendant's batch callback (same thread, same domain) is refused
        /// with the rule layer's message; the batch's later calls work, it commits its own row, and the ancestor's
        /// write made after the batch moves no descendant's Version.
        /// </summary>
        [Fact]
        public void AnAncestorWriteInsideADescendantsBatchIsRefused()
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

    }

}
