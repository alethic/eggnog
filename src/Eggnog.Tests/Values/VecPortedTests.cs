using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Vectors of values, ported from egglog's <c>tests/vec.egg</c> (lines 1-28 and 64-65 at 5e655cae); its rebuilding
    /// part (lines 30-62) waits for vectors of classes, and its last part (lines 67-82) for function values.
    /// </summary>
    public sealed class VecPortedTests
    {

        /// <summary>
        /// tests/vec.egg, lines 1-28 and 64-65: the <c>vec-*</c> primitives over <c>(Vec i64)</c>.
        /// </summary>
        [Fact]
        public void VectorsOfValues()
        {
            var graph = new EGraph();

            // (sort IVec (Vec i64))
            Assert.Equal("Vec<i64>", graph.Values.RegisterVec<long>().Name);
            var of = Primitives.VecOf<long>();
            var root = graph.Root;

            // (check (= (vec-of 1 2) (vec-push (vec-push (vec-empty) 1) 2)))
            var push = Primitives.VecPush<long>();
            Assert.True(root.Check(of.Of(1L, 2L).Is(push.Of(push.Of(Primitives.VecEmpty<long>().Of(), 1L), 2L))));

            // (check (= (vec-append (vec-of 1 2) (vec-of 3 4)) (vec-of 1 2 3 4)))
            Assert.True(root.Check(Primitives.VecAppend<long>().Of(of.Of(1L, 2L), of.Of(3L, 4L)).Is(of.Of(1L, 2L, 3L, 4L))));

            // (check (= (vec-pop (vec-of 1 2 3)) (vec-of 1 2))) and the regression of line 13
            var pop = Primitives.VecPop<long>();
            Assert.True(root.Check(pop.Of(of.Of(1L, 2L, 3L)).Is(of.Of(1L, 2L))));
            Assert.True(root.Check(of.Of(1L, 2L).Is(pop.Of(of.Of(1L, 2L, 3L)))));

            // (check (vec-not-contains (vec-of 1 2 3) 4)) (check (vec-contains (vec-of 1 2 3) 2))
            Assert.True(root.Check(Primitives.VecNotContains<long>().Of(of.Of(1L, 2L, 3L), 4L).Is(true)));
            Assert.True(root.Check(Primitives.VecContains<long>().Of(of.Of(1L, 2L, 3L), 2L).Is(true)));

            // (check (= (vec-length (vec-of 1 2 3)) 3)) (check (= (vec-get (vec-of 1 2 3) 1) 2))
            Assert.True(root.Check(Primitives.VecLength<long>().Of(of.Of(1L, 2L, 3L)).Is(3L)));
            Assert.True(root.Check(Primitives.VecGet<long>().Of(of.Of(1L, 2L, 3L), 1L).Is(2L)));

            // (check (= (vec-set (vec-of 1 2 3) 1 4) (vec-of 1 4 3)))
            Assert.True(root.Check(Primitives.VecSet<long>().Of(of.Of(1L, 2L, 3L), 1L, 4L).Is(of.Of(1L, 4L, 3L))));

            // (check (= (vec-range 5) (vec-of 0 1 2 3 4)))
            Assert.True(root.Check(Primitives.VecRange.Of(5L).Is(of.Of(0L, 1L, 2L, 3L, 4L))));
        }

    }

}
