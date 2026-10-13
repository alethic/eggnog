using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="UnionFindOverlay.Rekey"/>, which re-keys a child's classes after its parent merges, through
    /// <see cref="OverlayRekeyHarness"/>, which drives an overlay as a context's catch-up does: seven hand-written
    /// cases (a non-canonical member or a canonical displaced in the parent, a class dissolved or two classes joined by
    /// the parent, a trivial id imported, a chain of parent unions in one batch, and a dissolved class joining
    /// another), then random programs of parent unions, child unions and catch-ups replayed against a naive partition.
    /// </summary>
    public class UnionFindOverlayRekeyTests
    {

        static uint[] Class(OverlayRekeyHarness h, uint id)
        {
            Assert.True(h.Overlay.TryGetClass(id, out var slot));
            return h.Overlay.MembersOf(slot).ToArray().OrderBy(x => x).ToArray();
        }

        [Fact]
        public void ANonCanonicalMemberDisplacedInTheParent()
        {
            var h = new OverlayRekeyHarness();
            h.ChildUnion(2, 5);
            h.ParentUnion(5, 3);
            Assert.Equal(new uint[] { 3 }, h.CatchUp());
            Assert.Equal(new uint[] { 2, 3 }, Class(h, 3));
            Assert.False(h.Overlay.TryGetClass(5, out _));
            Assert.Equal(2u, h.Find(5));
            Assert.Equal(2u, h.Find(3));
            Assert.Equal(2u, h.Find(2));
        }

        [Fact]
        public void ACanonicalDisplacedInTheParent()
        {
            var h = new OverlayRekeyHarness();
            h.ChildUnion(2, 5);
            h.ParentUnion(2, 1);
            Assert.Equal(new uint[] { 2 }, h.CatchUp());
            Assert.Equal(new uint[] { 1, 5 }, Class(h, 1));
            Assert.Equal(1u, h.Find(5));
            Assert.Equal(1u, h.Find(2));
        }

        [Fact]
        public void AClassDissolvesWhenTheParentMergesItsMembers()
        {
            var h = new OverlayRekeyHarness();
            h.ChildUnion(2, 5);
            var version = h.Overlay.Version;
            h.ParentUnion(5, 2);
            Assert.Empty(h.CatchUp());
            Assert.True(h.Overlay.IsEmpty);
            Assert.Equal(version + 1, h.Overlay.Version);
            Assert.Equal(2u, h.Find(5));

            // 5 was journaled by the child's own union, and is not journaled twice.
            Assert.Equal(new uint[] { 5 }, h.Journal.Ids.ToArray());
        }

        [Fact]
        public void TwoClassesJoinedByTheParent()
        {
            var h = new OverlayRekeyHarness();
            h.ChildUnion(2, 5);
            h.ChildUnion(3, 7);
            h.ChildUnion(3, 8);
            h.ParentUnion(5, 7);
            Assert.Equal(new uint[] { 3 }, h.CatchUp());
            Assert.Equal(1, h.Overlay.ClassCount);
            Assert.Equal(new uint[] { 2, 3, 5, 8 }, Class(h, 2));
            foreach (var x in new uint[] { 2, 3, 5, 7, 8 })
                Assert.Equal(2u, h.Find(x));
        }

        [Fact]
        public void ATrivialIdDisplacedInTheParentIsAnImport()
        {
            var h = new OverlayRekeyHarness();
            h.ChildUnion(1, 2);
            var version = h.Overlay.Version;
            h.ParentUnion(9, 4);
            Assert.Equal(new uint[] { 9 }, h.CatchUp());
            Assert.Equal(4u, h.Find(9));
            Assert.Equal(1, h.Overlay.ClassCount);

            // Nothing in the overlay changed.
            Assert.Equal(version, h.Overlay.Version);
        }

        [Fact]
        public void AChainInOneBatchUsesTheFinalParentCanonical()
        {
            var h = new OverlayRekeyHarness();
            h.ChildUnion(9, 11);

            // d1 → p1 → p2 in one batch: 9 → 6, then 6 → 2.
            h.ParentUnion(9, 6);
            h.ParentUnion(6, 2);
            Assert.Equal(new uint[] { 9, 6 }, h.CatchUp());
            Assert.Equal(new uint[] { 2, 11 }, Class(h, 2));
            foreach (var x in new uint[] { 2, 6, 9, 11 })
                Assert.Equal(2u, h.Find(x));
        }

        [Fact]
        public void ADissolvedClassJoinsAnother()
        {
            var h = new OverlayRekeyHarness();
            h.ChildUnion(3, 8);
            h.ChildUnion(2, 6);
            h.ParentUnion(8, 6);
            Assert.Equal(new uint[] { 3 }, h.CatchUp());
            Assert.Equal(1, h.Overlay.ClassCount);
            Assert.Equal(new uint[] { 2, 3, 6 }, Class(h, 2));
            foreach (var x in new uint[] { 2, 3, 6, 8 })
                Assert.Equal(2u, h.Find(x));
        }

        /// <summary>
        /// The seeds of the random property.
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var data = new TheoryData<int>();
            for (var seed = 0; seed < 200; seed++)
                data.Add(seed);

            return data;
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void RandomProgramsAgreeWithANaiveReplay(int seed)
        {
            var random = new Random(seed);
            var size = random.Next(8, 60);
            var h = new OverlayRekeyHarness();
            var all = new PartitionReference(size);
            var universe = Enumerable.Range(0, size).Select(x => (uint)x).ToArray();
            var canonical = universe.ToHashSet();

            // After every catch-up: the overlay over the parent equals the replay of every union; the ids journaled are
            // exactly those canonical in the child at the last sync and not at this one; the journal holds exactly the
            // ids that are not canonical in the child (the parent started empty), once each; and every member is
            // canonical in the parent, in a class of at least two whose canonical is its minimum.
            void Sync(List<uint> journaled)
            {
                foreach (var x in universe)
                    Assert.Equal(all.Canonical(x), h.Find(x));

                var now = universe.Where(x => h.Find(x) == x).ToHashSet();
                Assert.Equal(canonical.Except(now).OrderBy(x => x), journaled.OrderBy(x => x));
                canonical = now;

                var nonCanonical = universe.Where(x => all.Canonical(x) != x).ToArray();
                Assert.Equal(nonCanonical, h.Journal.Ids.ToArray().OrderBy(x => x).ToArray());
                for (var slot = 0; slot < h.Overlay.SlotCount; slot++)
                {
                    var members = h.Overlay.MembersOf(slot).ToArray();
                    if (members.Length == 0)
                        continue;

                    Assert.True(members.Length >= 2);
                    Assert.Equal(members.Min(), h.Overlay.CanonicalOf(slot));
                    Assert.All(members, m => Assert.Equal(m, h.Parent.FindNaive(m)));
                }
            }

            for (var step = 0; step < 200; step++)
            {
                var a = (uint)random.Next(size);
                var b = (uint)random.Next(size);
                switch (random.Next(10))
                {
                    case < 4:
                        h.ParentUnion(a, b);
                        all.Union(a, b, out _, out _);
                        break;
                    case < 8:
                        Sync(h.CatchUp());
                        h.ChildUnion(a, b);
                        all.Union(a, b, out _, out _);
                        canonical = universe.Where(x => h.Find(x) == x).ToHashSet();
                        break;
                    default:
                        Sync(h.CatchUp());
                        break;
                }
            }

            Sync(h.CatchUp());
        }

    }

}
