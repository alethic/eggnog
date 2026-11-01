using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Sets of values, ported from egglog's <c>tests/web-demo/set.egg</c> (lines 1-13 and 21-22 at 5e655cae); its
    /// <c>set-get</c> part (lines 15-19) and its reifying part (lines 24-40) read <c>set-get</c>'s cell order, which
    /// egglog's representations set.
    /// </summary>
    public sealed class SetPortedTests
    {

        /// <summary>
        /// tests/web-demo/set.egg, lines 1-13 and 21-22: the <c>set-*</c> primitives over <c>(Set i64)</c>, the empty set
        /// <c>set-empty</c> among them.
        /// </summary>
        [Fact]
        public void SetsOfValues()
        {
            var graph = new EGraph();

            // (sort ISetBase (Set i64)), which the graph registers itself
            var of = Primitives.SetOf<long>();
            var empty = Primitives.SetEmpty<long>().Of();
            var insert = Primitives.SetInsert<long>();
            var root = graph.Root;

            // (check (= (set-of 1 2) (set-insert (set-insert (set-empty) 1) 2)))
            // (check (= (set-of 1 2) (set-insert (set-insert (set-empty) 2) 1)))
            Assert.True(root.Check(of.Of(1L, 2L).Is(insert.Of(insert.Of(empty, 1L), 2L))));
            Assert.True(root.Check(of.Of(1L, 2L).Is(insert.Of(insert.Of(empty, 2L), 1L))));

            // (check (= (set-union (set-of 1 2) (set-of 3 4)) (set-of 1 2 3 4)))
            Assert.True(root.Check(Primitives.SetUnion<long>().Of(of.Of(1L, 2L), of.Of(3L, 4L)).Is(of.Of(1L, 2L, 3L, 4L))));

            // (check (= 0 (set-length (set-empty)))) (check (= 1 (set-length (set-of 1 1 1))))
            // (check (= 2 (set-length (set-of 1 -1 1 1))))
            var length = Primitives.SetLength<long>();
            Assert.True(root.Check(length.Of(empty).Is(0L)));
            Assert.True(root.Check(length.Of(of.Of(1L, 1L, 1L)).Is(1L)));
            Assert.True(root.Check(length.Of(of.Of(1L, -1L, 1L, 1L)).Is(2L)));

            // (check (= (set-remove (set-of 1 2 3) 3) (set-of 1 2)))
            Assert.True(root.Check(Primitives.SetRemove<long>().Of(of.Of(1L, 2L, 3L), 3L).Is(of.Of(1L, 2L))));

            // The empty set is set-of of no elements, and the empty value
            Assert.True(root.Check(empty.Is(of.Of())));
            Assert.True(root.Check(empty.Is(Set<long>.Empty)));
        }

    }

}
