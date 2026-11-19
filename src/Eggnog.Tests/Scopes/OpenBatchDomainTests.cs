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
        public void ABatchDoesNotFlushWhileAScopeUnderItHasABatchOpen()
        {
            // A call that needs the batch's union rebuilt flushes it, which would change what the child's open batch
            // was checked against: it is refused, as a write of the root itself is then.
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, Node>("f");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var child = t.Root.CreateChild();
            t.Root.Write(w =>
            {
                w.Union(a, b);
                child.Write(cw =>
                {
                    cw.Assume(t.Num.Of(1L));
                    var refused = Assert.Throws<InvalidOperationException>(() => w.Assume(f.Of(1L).Is(t.A.Of())));
                    Assert.Contains("has a Write batch open", refused.Message, StringComparison.Ordinal);
                });
            });

            Assert.True(t.Root.AreEqual(a, b));
            Assert.Equal(0, t.Root.CountRows(f));
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

            Assert.True(inner.Check(t.Num.Of(1L)));
            Assert.Equal(5, inner.CountRows(t.Num));
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

        /// <summary>
        /// An ancestor's query that leaves the descendant behind it, made inside the descendant's batch, before one of
        /// its calls or after the last, leaves the batch usable and its Version moved as by the same query made before
        /// the batch: the batch catches up before its next call, or as it commits, outside Version.
        /// </summary>
        /// <param name="callsBefore">How many of the batch's two calls come before the query.</param>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void AnAncestorsQueryInsideTheBatchLeavesTheBatchUsableAndItsVersionAsWithoutTheQuery(int callsBefore)
        {
            Assert.Equal(BatchVersionDelta(null), BatchVersionDelta(callsBefore));
        }

        /// <summary>
        /// How far a batch of two new rows moves a descendant's Version, with an ancestor's query made before the batch
        /// (<paramref name="callsBefore"/> <c>null</c>) or inside it, after that many of its calls. The query builds a
        /// vector of each class it matches, which interns the vectors in the ancestor; the descendant merged the two
        /// classes, so the new vectors leave it behind the ancestor until it catches up with them.
        /// </summary>
        static long BatchVersionDelta(int? callsBefore)
        {
            var t = new NodeTheory();
            t.Graph.Values.RegisterVec<Node>();
            var vectors = t.Graph.DefineQuery("vectors", q =>
            {
                var (x, v) = q.Vars<Node, Vec<Node>>("x", "v");
                q.When(t.Tag.Of(x), v.Is(Primitives.VecOf<Node>().Of(x)));
            });
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));
            inner.Union(a, b);

            // Asks the ancestor's query, and checks that the vectors it interned left the descendant behind it.
            void AskTheAncestor()
            {
                Assert.Equal(2, outer.Count(vectors));
                Assert.True(inner.Engine.Context.Context.Tables!.NeedsCatchUp);
            }

            if (callsBefore is null)
                AskTheAncestor();

            var version = inner.Version;
            inner.Write(w =>
            {
                if (callsBefore == 0)
                    AskTheAncestor();

                w.Assume(t.Num.Of(1L));
                if (callsBefore == 1)
                    AskTheAncestor();

                w.Assume(t.Num.Of(2L));
                if (callsBefore == 2)
                    AskTheAncestor();
            });

            Assert.Equal(2, inner.CountRows(t.Num));
            Assert.Equal(2, outer.Count(vectors));
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
