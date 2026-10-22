using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Pairs of values, ported from egglog's <c>tests/pair.egg</c> (lines 1-17 at 5e655cae); its last part (lines 19-29),
    /// a pair of an e-class and a value, waits for pairs of classes.
    /// </summary>
    public sealed class PairPortedTests
    {

        /// <summary>
        /// tests/pair.egg, lines 1-17: <c>pair</c>, <c>pair-first</c> and <c>pair-second</c> over <c>(Pair i64 i64)</c>
        /// and <c>(Pair i64 String)</c>, and pairs compared by equality.
        /// </summary>
        [Fact]
        public void PairsOfValues()
        {
            var graph = new EGraph();

            // (sort IntPair (Pair i64 i64))
            Assert.Equal("Pair<i64, i64>", graph.Values.RegisterPair<long, long>().Name);
            var pair = Primitives.PairOf<long, long>();
            var first = Primitives.PairFirst<long, long>();
            var second = Primitives.PairSecond<long, long>();
            var p = graph.DefineFunction<Pair<long, long>>("p");
            var q = graph.DefineFunction<Pair<long, long>>("q");

            // (let p (pair 1 2)) (check (= (pair-first p) 1)) (check (= (pair-second p) 2))
            graph.Root.Assume(p.Of().Is(pair.Of(1L, 2L)));
            Assert.True(graph.Root.Check(first.Of(p.Of()).Is(1L)));
            Assert.True(graph.Root.Check(second.Of(p.Of()).Is(2L)));

            // (let q (pair 3 4)) (check (= (pair-first q) 3)) (check (= (pair-second q) 4))
            graph.Root.Assume(q.Of().Is(pair.Of(3L, 4L)));
            Assert.True(graph.Root.Check(first.Of(q.Of()).Is(3L)));
            Assert.True(graph.Root.Check(second.Of(q.Of()).Is(4L)));

            // (check (= p (pair 1 2))) (check (!= p q))
            Assert.True(graph.Root.Check(p.Of().Is(pair.Of(1L, 2L))));
            Assert.True(graph.Root.Check(Primitives.NotEqual<Pair<long, long>>().Of(p.Of(), q.Of()).Is(true)));
            Assert.False(graph.Root.Check(p.Of().Is(q.Of())));

            // (sort StrPair (Pair i64 String)) (let sp (pair 10 "hello"))
            graph.Values.RegisterPair<long, string>();
            var sp = graph.DefineFunction<Pair<long, string>>("sp");
            graph.Root.Assume(sp.Of().Is(Primitives.PairOf<long, string>().Of(10L, "hello")));

            // (check (= (pair-first sp) 10)) (check (= (pair-second sp) "hello"))
            Assert.True(graph.Root.Check(Primitives.PairFirst<long, string>().Of(sp.Of()).Is(10L)));
            Assert.True(graph.Root.Check(Primitives.PairSecond<long, string>().Of(sp.Of()).Is("hello")));
            Assert.Equal(new Pair<long, string>(10, "hello"), graph.Root.Get(sp.Of()));
        }

    }

}
