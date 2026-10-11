using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="UnionFindOverlay"/>'s class table (colored-contexts spec §5.2): the three shapes of a union,
    /// canonical = minimum member, member lists in insertion order, the class map, slot reuse, the version, freezing,
    /// and a random property test against <see cref="PartitionReference"/>.
    /// </summary>
    public class UnionFindOverlayTests
    {

        static uint[] Members(UnionFindOverlay overlay, uint id)
        {
            Assert.True(overlay.TryGetClass(id, out var slot));
            return overlay.MembersOf(slot).ToArray();
        }

        [Fact]
        public void AnEmptyOverlayMapsEveryIdToItself()
        {
            var overlay = new UnionFindOverlay();
            Assert.True(overlay.IsEmpty);
            Assert.Equal(0, overlay.MemberCount);
            Assert.Equal(0, overlay.ClassCount);
            Assert.Equal(0, overlay.SlotCount);
            Assert.Equal(0, overlay.Version);
            Assert.Equal(7u, overlay.Map(7));
            Assert.Equal(uint.MaxValue, overlay.Map(uint.MaxValue));
            Assert.False(overlay.TryGetClass(7, out var slot));
            Assert.Equal(-1, slot);
            Assert.Throws<ArgumentOutOfRangeException>(() => overlay.CanonicalOf(0));
        }

        [Fact]
        public void UnionCreatesExtendsAndMergesClasses()
        {
            var overlay = new UnionFindOverlay();

            // A new class of two trivial ids; the smaller wins.
            Assert.True(overlay.Union(9, 4, out var winner, out var loser));
            Assert.Equal((4u, 9u), (winner, loser));
            Assert.False(overlay.IsEmpty);
            Assert.Equal(new uint[] { 9, 4 }, Members(overlay, 4));
            Assert.Equal(4u, overlay.Map(9));
            Assert.Equal(4u, overlay.Map(4));
            Assert.Equal(1, overlay.Version);

            // A trivial id joins: the class's canonical against the id.
            Assert.True(overlay.Union(2, 9, out winner, out loser));
            Assert.Equal((2u, 4u), (winner, loser));
            Assert.Equal(new uint[] { 9, 4, 2 }, Members(overlay, 9));
            Assert.Equal(2u, overlay.Map(4));
            Assert.Equal(2u, overlay.Map(9));

            // A second class, then the two merge: the smaller class's members move.
            Assert.True(overlay.Union(1, 30, out winner, out loser));
            Assert.Equal((1u, 30u), (winner, loser));
            Assert.Equal(2, overlay.ClassCount);
            Assert.True(overlay.Union(30, 4, out winner, out loser));
            Assert.Equal((1u, 2u), (winner, loser));
            Assert.Equal(1, overlay.ClassCount);
            Assert.Equal(5, overlay.MemberCount);
            Assert.Equal(new uint[] { 9, 4, 2, 1, 30 }, Members(overlay, 1));
            foreach (var id in new uint[] { 9, 4, 2, 1, 30 })
                Assert.Equal(1u, overlay.Map(id));

            Assert.True(overlay.TryGetClass(30, out var slot));
            Assert.Equal(1u, overlay.CanonicalOf(slot));
            Assert.Equal(5, overlay.CountOf(slot));
            Assert.Equal(4, overlay.Version);
        }

        [Fact]
        public void RedundantUnionsChangeNothing()
        {
            var overlay = new UnionFindOverlay();
            Assert.False(overlay.Union(3, 3, out var winner, out var loser));
            Assert.Equal((3u, 3u), (winner, loser));
            Assert.True(overlay.IsEmpty);
            Assert.Equal(0, overlay.Version);

            overlay.Union(3, 8, out _, out _);
            Assert.False(overlay.Union(8, 3, out winner, out loser));
            Assert.Equal((3u, 3u), (winner, loser));
            Assert.Equal(1, overlay.Version);
            Assert.Equal(2, overlay.MemberCount);
        }

        [Fact]
        public void TheSmallerClassMovesIntoTheLarger()
        {
            var overlay = new UnionFindOverlay();
            overlay.Union(10, 11, out _, out _);
            overlay.Union(10, 12, out _, out _);
            overlay.Union(10, 13, out _, out _);
            overlay.TryGetClass(10, out var big);
            overlay.Union(1, 2, out _, out _);

            // The larger class keeps its slot even though the smaller holds the new canonical.
            overlay.Union(2, 13, out var winner, out var loser);
            Assert.Equal((1u, 10u), (winner, loser));
            Assert.True(overlay.TryGetClass(1, out var merged));
            Assert.Equal(big, merged);
            Assert.Equal(new uint[] { 10, 11, 12, 13, 1, 2 }, overlay.MembersOf(merged).ToArray());
        }

        [Fact]
        public void FreedSlotsAreReused()
        {
            var overlay = new UnionFindOverlay();
            overlay.Union(1, 2, out _, out _);
            overlay.Union(3, 4, out _, out _);
            Assert.Equal(2, overlay.SlotCount);
            overlay.Union(1, 3, out _, out _);
            Assert.Equal(1, overlay.ClassCount);
            Assert.Equal(2, overlay.SlotCount);

            // The merged-away slot is free and taken by the next class.
            var free = Enumerable.Range(0, overlay.SlotCount).Single(s => overlay.CountOf(s) == 0);
            Assert.True(overlay.MembersOf(free).IsEmpty);
            overlay.Union(5, 6, out _, out _);
            Assert.True(overlay.TryGetClass(5, out var reused));
            Assert.Equal(free, reused);
            Assert.Equal(2, overlay.SlotCount);
        }

        [Fact]
        public void AFrozenOverlayRefusesWritesAndStillMaps()
        {
            var overlay = new UnionFindOverlay();
            overlay.Union(5, 2, out _, out _);
            overlay.Freeze();
            Assert.True(overlay.IsFrozen);
            Assert.Throws<InvalidOperationException>(() => overlay.Union(1, 2, out _, out _));
            Assert.Throws<InvalidOperationException>(() => overlay.Rekey(5, 1, stackalloc uint[2]));
            Assert.Equal(2u, overlay.Map(5));
        }

        [Fact]
        public void ReleaseDropsEveryClass()
        {
            var overlay = new UnionFindOverlay();
            overlay.Union(5, 2, out _, out _);
            overlay.Release();
            Assert.True(overlay.IsEmpty);
            Assert.Equal(0, overlay.MemberCount);
            Assert.Equal(5u, overlay.Map(5));
        }

        [Fact]
        public void RekeyNeedsRoomForTwoIds()
        {
            var overlay = new UnionFindOverlay();
            Assert.Throws<ArgumentException>(() => overlay.Rekey(5, 1, new uint[1]));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void RandomUnionsAgreeWithAPartition(int seed)
        {
            const int Size = 300;
            var random = new Random(seed);
            var overlay = new UnionFindOverlay();
            var reference = new PartitionReference(Size);
            var touched = new HashSet<uint>();
            for (var step = 0; step < 600; step++)
            {
                var a = (uint)random.Next(Size);
                var b = (uint)random.Next(Size);
                var effective = overlay.Union(a, b, out var winner, out var loser);
                Assert.Equal(reference.Union(a, b, out var parent, out var child), effective);
                Assert.Equal((parent, child), (winner, loser));
                touched.Add(a);
                touched.Add(b);

                if (step % 50 != 49)
                    continue;

                for (uint x = 0; x < Size; x++)
                    Assert.Equal(reference.Canonical(x), overlay.Map(x));

                // Every member is in exactly one class of at least two, keyed in the class map, with its minimum as
                // canonical; the trivial ids are in none.
                var seen = new HashSet<uint>();
                var classes = 0;
                for (var slot = 0; slot < overlay.SlotCount; slot++)
                {
                    var members = overlay.MembersOf(slot).ToArray();
                    if (members.Length == 0)
                        continue;

                    classes++;
                    Assert.True(members.Length >= 2);
                    Assert.Equal(members.Min(), overlay.CanonicalOf(slot));
                    foreach (var m in members)
                    {
                        Assert.True(seen.Add(m));
                        Assert.True(overlay.TryGetClass(m, out var k));
                        Assert.Equal(slot, k);
                    }
                }

                Assert.Equal(classes, overlay.ClassCount);
                Assert.Equal(seen.Count, overlay.MemberCount);
                for (uint x = 0; x < Size; x++)
                    Assert.Equal(seen.Contains(x), reference.Canonical(x) != x || Enumerable.Range(0, Size).Any(y => y != x && reference.Canonical((uint)y) == x));
            }
        }

    }

}
