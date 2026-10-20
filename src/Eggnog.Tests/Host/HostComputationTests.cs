using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// Host writes and reads compute and read function values, as egglog's top-level actions and <c>check</c> do: a
    /// primitive is evaluated on the calling thread, a function's value is read, and a read later in a batch sees the
    /// batch's earlier writes, merged; a read never interns.
    /// </summary>
    public sealed class HostComputationTests
    {

        [Fact]
        public void AWriteComputes()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            t.Root.Assume(f.Of(1L).Is(Primitives.Add.Of(2L, 3L)), t.Num.Of(Primitives.Multiply.Of(2L, 3L)));
            var lit = t.Root.Make(t.Lit.Of(Primitives.Subtract.Of(10L, 7L)));

            Assert.Equal(5L, t.Root.Get(f.Of(1L)));
            Assert.True(t.Root.Check(t.Num.Of(6L)));
            Assert.True(t.Root.AreEqual(lit, t.Lit.Of(3L)));
        }

        /// <summary>
        /// egglog's <c>(set (g 1) (+ (f 1) 1))</c>: a write reads a function's value.
        /// </summary>
        [Fact]
        public void AWriteReadsAFunction()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var g = t.Graph.DefineFunction<long, long>("g");
            t.Root.Assume(f.Of(1L).Is(5L));

            t.Root.Assume(g.Of(1L).Is(Primitives.Add.Of(f.Of(1L), 1L)));

            Assert.Equal(6L, t.Root.Get(g.Of(1L)));
        }

        /// <summary>
        /// A read later in a batch sees the batch's earlier writes, as egglog's next command sees the last: a value written
        /// first, and a lattice's value merged with the stored one.
        /// </summary>
        [Fact]
        public void AReadSeesTheBatchsEarlierWritesMerged()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var g = t.Graph.DefineFunction<long, long>("g");
            var high = t.Graph.DefineFunction<long, long>("high", ValueLattice.Max<long>());
            var copy = t.Graph.DefineFunction<long, long>("copy");
            t.Root.Assume(high.Of(1L).Is(5L));

            t.Root.Assume(f.Of(1L).Is(7L), g.Of(1L).Is(f.Of(1L)), high.Of(1L).Is(3L), copy.Of(1L).Is(high.Of(1L)));

            Assert.Equal(7L, t.Root.Get(g.Of(1L)));
            Assert.Equal(5L, t.Root.Get(copy.Of(1L)));
        }

        /// <summary>
        /// A batch stages its merges of classes after its writes, so after a merge it cannot read a function of classes
        /// as egglog's next command would, with the rows the merge makes equal merged: the read is refused, and the batch
        /// writes nothing. A function of values may still be read, and two calls give egglog's answer.
        /// </summary>
        [Fact]
        public void AReadOfAFunctionOfClassesAfterAMergeInTheBatchIsRefused()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, long>("f");
            var g = t.Graph.DefineFunction<long, long>("g");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(f.Of(b).Is(7L), g.Of(1L).Is(1L));

            var e = Assert.Throws<InvalidOperationException>(() => t.Root.Assume(t.A.Of().Is(t.B.Of()), g.Of(2L).Is(f.Of(a))));

            Assert.Contains("read it in a later call", e.Message);
            Assert.False(t.Root.AreEqual(a, t.B.Of()));
            Assert.False(t.Root.IsFaulted);

            t.Root.Assume(t.A.Of().Is(t.Lit.Of(1L)), g.Of(3L).Is(Primitives.Add.Of(g.Of(1L), 1L)));
            Assert.Equal(2L, t.Root.Get(g.Of(3L)));
            t.Root.Assume(t.A.Of().Is(t.B.Of()));
            t.Root.Assume(g.Of(2L).Is(f.Of(a)));
            Assert.Equal(7L, t.Root.Get(g.Of(2L)));
        }

        [Fact]
        public void AMissingValueRefusesTheWholeBatch()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var g = t.Graph.DefineFunction<long, long>("g");

            var e = Assert.Throws<InvalidOperationException>(() => t.Root.Assume(t.Num.Of(1L), g.Of(1L).Is(f.Of(2L))));

            Assert.Equal("(f 2) has no value here.", e.Message);
            Assert.False(t.Root.Check(t.Num.Of(1L)));
            Assert.False(t.Root.IsFaulted);
        }

        /// <summary>
        /// A primitive with no result, or one that throws, refuses the whole batch without faulting the scope, as egglog's
        /// top-level action fails with an error.
        /// </summary>
        [Fact]
        public void APrimitiveWithNoResultRefusesTheWholeBatch()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var boom = Primitive.Create<long, long>("boom", static _ => throw new InvalidOperationException("boom"));

            var none = Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(t.Num.Of(1L), f.Of(1L).Is(Primitives.Divide.Of(1L, 0L))));
            var threw = Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(f.Of(1L).Is(boom.Of(1L))));

            Assert.Contains("primitive '/' has no result", none.Message);
            Assert.IsType<InvalidOperationException>(threw.InnerException);
            Assert.Equal(0, t.Root.CountRows(t.Num));
            Assert.Equal(0, t.Root.CountRows(f));
            Assert.False(t.Root.IsFaulted);
        }

        /// <summary>
        /// A read computes too, as egglog's <c>check</c> does, but never interns: a result no write interned is no value,
        /// so the fact is false.
        /// </summary>
        [Fact]
        public void AReadComputesWithoutInterning()
        {
            var graph = new EGraph();
            var name = graph.DefineFunction<long, string>("name");
            graph.Root.Assume(name.Of(1L).Is("ab"));
            var interned = graph.Values.InternedCount;

            Assert.True(graph.Root.Check(name.Of(1L).Is(StringPrimitives.Concat.Of("a", "b"))));
            Assert.False(graph.Root.Check(name.Of(1L).Is(StringPrimitives.Concat.Of("x", "y"))));
            Assert.False(graph.Root.TryGet(StringPrimitives.Concat.Of("x", "y"), out _));
            Assert.Equal("ab", graph.Root.Get(StringPrimitives.Concat.Of("a", "b")));
            Assert.False(graph.Root.Check(name.Of(Primitives.Divide.Of(1L, 0L)).Is("ab")));
            Assert.Equal(interned, graph.Values.InternedCount);
        }

    }

}
