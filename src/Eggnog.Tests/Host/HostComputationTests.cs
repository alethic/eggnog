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
        /// A read of a function of classes after a merge in the batch sees the rows the merge makes equal merged, as
        /// egglog's next command does after the union's rebuild: (set (f (B)) 7) (union (A) (B)) (set (g 2) (f (A))) gives
        /// 7. A function of values reads as before.
        /// </summary>
        [Fact]
        public void AReadOfAFunctionOfClassesAfterAMergeInTheBatchSeesTheMerge()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<Node, long>("f");
            var g = t.Graph.DefineFunction<long, long>("g");
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(f.Of(b).Is(7L), g.Of(1L).Is(1L));

            t.Root.Assume(t.A.Of().Is(t.B.Of()), g.Of(2L).Is(f.Of(a)));

            Assert.True(t.Root.AreEqual(a, t.B.Of()));
            Assert.Equal(7L, t.Root.Get(g.Of(2L)));
            t.Root.Assume(t.A.Of().Is(t.Lit.Of(1L)), g.Of(3L).Is(Primitives.Add.Of(g.Of(1L), 1L)));
            Assert.Equal(2L, t.Root.Get(g.Of(3L)));
        }

        [Fact]
        public void AMissingValueRefusesItsFact()
        {
            // (num 1) stands; (set (g 1) (f 2)) fails, as egglog's command does.
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var g = t.Graph.DefineFunction<long, long>("g");

            var e = Assert.Throws<InvalidOperationException>(() => t.Root.Assume(t.Num.Of(1L), g.Of(1L).Is(f.Of(2L))));

            Assert.Equal("(f 2) has no value here.", e.Message);
            Assert.True(t.Root.Check(t.Num.Of(1L)));
            Assert.Equal(0, t.Root.CountRows(g));
            Assert.False(t.Root.IsFaulted);
        }

        /// <summary>
        /// A primitive with no result, or one that throws, refuses its fact without faulting the scope, as egglog's
        /// top-level action fails with an error; the facts before it stand.
        /// </summary>
        [Fact]
        public void APrimitiveWithNoResultRefusesItsFact()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            var boom = Primitive.Create<long, long>("boom", static _ => throw new InvalidOperationException("boom"));

            var none = Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(t.Num.Of(1L), f.Of(1L).Is(Primitives.Divide.Of(1L, 0L))));
            var threw = Assert.Throws<RuleEvaluationException>(() => t.Root.Assume(f.Of(1L).Is(boom.Of(1L))));

            Assert.Contains("primitive '/' has no result", none.Message);
            Assert.IsType<InvalidOperationException>(threw.InnerException);
            Assert.Equal(1, t.Root.CountRows(t.Num));
            Assert.Equal(0, t.Root.CountRows(f));
            Assert.False(t.Root.IsFaulted);
        }

        /// <summary>
        /// A read computes too, as egglog's <c>check</c> does, but never interns, and answers as egglog, which interns: a
        /// result no write interned is a value, which reads back as itself and equals only itself, and is no row's
        /// output.
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
            Assert.True(graph.Root.TryGet(StringPrimitives.Concat.Of("x", "y"), out var xy));
            Assert.Equal("xy", xy);
            Assert.True(graph.Root.Check(StringPrimitives.Concat.Of("x", "y").Is("xy")));
            Assert.False(graph.Root.Check(StringPrimitives.Concat.Of("x", "y").Is("yx")));
            Assert.False(graph.Root.Check(StringPrimitives.Concat.Of("x", "y").Is("ab")));
            Assert.Equal("ab", graph.Root.Get(StringPrimitives.Concat.Of("a", "b")));
            Assert.False(graph.Root.Check(name.Of(Primitives.Divide.Of(1L, 0L)).Is("ab")));
            Assert.Equal(interned, graph.Values.InternedCount);
        }

    }

}
