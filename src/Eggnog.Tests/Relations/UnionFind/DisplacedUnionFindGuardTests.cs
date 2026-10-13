using System;
using System.Linq;
using System.Reflection;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="DisplacedUnionFind"/> keeps its documented guarantees: a rejected union changes nothing, and the
    /// union-find it hands out cannot displace an id without a row. In the allocation collection with
    /// <see cref="ConcurrentUnionFindAllocationTests"/>.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class DisplacedUnionFindGuardTests
    {

        /// <summary>
        /// A rejected union changes nothing: <see cref="DisplacedUnionFind.Union"/> checks the timestamp before its two
        /// mutating finds, which would reserve both ids and halve paths, so the rejected call does not grow the
        /// union-find.
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
        /// The same through path halving: a rejected union over a chain leaves the parent links as they were.
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
        /// through which only <c>find_naive</c> can be called. <see cref="DisplacedUnionFind.UnionFind"/> hands out a
        /// <see cref="ReadOnlyUnionFind"/>, so a caller cannot displace an id without a row, which would break the
        /// invariant that every non-canonical id has exactly one row (incremental rebuild relies on it to find changed
        /// ids).
        /// </summary>
        /// <remarks>
        /// A write through the view does not compile, so the test checks the type: the view has no member that writes,
        /// and a union through the owner leaves a row for the displaced id.
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

    }

}
