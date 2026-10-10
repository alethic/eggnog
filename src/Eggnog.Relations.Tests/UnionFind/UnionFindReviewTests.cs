using System;
using System.Linq;
using System.Reflection;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial-review tests for the union-find module. Each test pins down a behavior the port documents or that
    /// egglog guarantees; a failure marks a defect.
    /// </summary>
    public class UnionFindReviewTests
    {

        /// <summary>
        /// <see cref="DisplacedUnionFind.Union"/> documents that "a rejected union changes nothing" (and the port
        /// report says the state is left unchanged), but it runs the two mutating finds before the timestamp check:
        /// they reserve both ids and halve paths, so the rejected call still grows the union-find.
        /// </summary>
        [Fact]
        public void RejectedUnionDoesNotReserveIds()
        {
            var d = new DisplacedUnionFind();
            d.Union(0, 1, 10, out _, out _);
            var idCount = d.UnionFind.IdCount;
            var parents = d.UnionFind.Parents.ToArray();

            Assert.Throws<InvalidOperationException>(() => d.Union(500, 501, 9, out _, out _));

            Assert.Equal(idCount, d.UnionFind.IdCount);
            Assert.Equal(parents, d.UnionFind.Parents.ToArray());
        }

        /// <summary>
        /// Same defect, through path halving: a rejected union over a chain rewrites parent links.
        /// </summary>
        [Fact]
        public void RejectedUnionDoesNotCompressPaths()
        {
            var d = new DisplacedUnionFind();
            d.Union(3, 4, 10, out _, out _);
            d.Union(2, 3, 10, out _, out _);
            d.Union(1, 2, 10, out _, out _);
            var parents = d.UnionFind.Parents.ToArray();
            Assert.Equal(new uint[] { 0, 1, 1, 2, 3 }, parents);

            Assert.Throws<InvalidOperationException>(() => d.Union(4, 0, 9, out _, out _));

            Assert.Equal(parents, d.UnionFind.Parents.ToArray());
        }

        /// <summary>
        /// egglog's <c>underlying_uf</c> (<c>core-relations/src/uf/mod.rs:463-465</c>) hands out <c>&amp;UnionFind</c>,
        /// through which only <c>find_naive</c> can be called. The port handed out the mutable union-find, so a caller
        /// could displace an id without a row, breaking the documented invariant that every non-canonical id has
        /// exactly one row (which incremental rebuild relies on to find changed ids).
        /// </summary>
        /// <remarks>
        /// The review's version of this test called <c>d.UnionFind.Union(5, 6)</c>. With the fix that call no longer
        /// compiles, so the test now checks the same thing through the type: the view has no member that writes, and
        /// a union through the owner leaves a row for the displaced id.
        /// </remarks>
        [Fact]
        public void UnderlyingUnionFindCannotDisplaceAnIdWithoutARow()
        {
            var property = typeof(DisplacedUnionFind).GetProperty(nameof(DisplacedUnionFind.UnionFind));
            Assert.NotNull(property);
            Assert.Equal(typeof(ReadOnlyUnionFind), property.PropertyType);

            var writers = new[] { "Union", "Find", "Reserve", "Reset", "Clone" };
            var members = typeof(ReadOnlyUnionFind)
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name);
            Assert.Empty(members.Intersect(writers));
            Assert.All(
                typeof(ReadOnlyUnionFind).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                f => Assert.True(f.IsInitOnly));

            var d = new DisplacedUnionFind();
            d.Union(5, 6, 0, out _, out _);
            Assert.Equal(5u, d.UnionFind.FindNaive(6));
            Assert.True(d.FindNaive(6) == 6 || d.TryGetRow(6, out _));
        }

        /// <summary>
        /// egglog's reset re-initializes the buffer in place (<c>union-find/src/concurrent/buffer.rs:91-98</c>). The
        /// port allocates a whole new set of segments on every reset, so a reset of a large union-find allocates (on
        /// the large object heap) as much as the union-find itself and transiently doubles its footprint.
        /// </summary>
        [Fact]
        public void ConcurrentResetDoesNotReallocateTheSlots()
        {
            var uf = new ConcurrentUnionFind(1 << 20);
            uf.Reset();

            var before = GC.GetAllocatedBytesForCurrentThread();
            uf.Reset();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < 64 * 1024, $"Reset allocated {allocated} bytes for a capacity of {uf.Capacity}.");
        }

    }

}
