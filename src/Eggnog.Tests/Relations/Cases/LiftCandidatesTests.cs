using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="LiftCandidates"/>, the lift's deduplicating arena: a reset costs what the arena last held, not its
    /// capacity, and keeps none of an earlier table's keys; against a model over random resets, every add reports what
    /// a set of keys reports, and the first entry per key is kept.
    /// </summary>
    public class LiftCandidatesTests
    {

        /// <summary>
        /// <see cref="LiftCandidates.Reset"/> clears only the slots the arena used once its slot table is large: after
        /// a large table, a small one deduplicates correctly and keeps none of the large one's keys.
        /// </summary>
        [Fact]
        public void ACandidateArenaResetAfterALargeTableKeepsNoStaleSlots()
        {
            var arena = new LiftCandidates();
            arena.Reset(2, 1);
            for (var i = 0u; i < 10_000; i++)
                Assert.True(arena.Add([i, i + 1]));

            Assert.False(arena.Add([5u, 0u]));

            arena.Reset(2, 1);
            Assert.Equal(0, arena.Count);
            Assert.True(arena.Add([5u, 1u]));
            Assert.True(arena.Add([9_999u, 1u]));
            Assert.False(arena.Add([5u, 2u]));
            Assert.Equal(2, arena.Count);

            // Few entries in a large table: the next reset clears them one by one.
            arena.Reset(3, 2);
            for (var i = 0u; i < 10_000; i += 1000)
                Assert.True(arena.Add([i, 7u, 0u]));

            Assert.False(arena.Add([3000u, 7u, 1u]));
            Assert.True(arena.Add([3000u, 8u, 1u]));
            arena.Reset(3, 2);
            for (var i = 0u; i < 10_000; i += 1000)
                Assert.True(arena.Add([i, 7u, 0u]), $"key ({i}, 7) was left behind by the last reset");

            Assert.Equal(10, arena.Count);
            Assert.Equal(new uint[] { 9000u, 7u, 0u }, arena[9].ToArray());
        }

        /// <summary>
        /// <see cref="LiftCandidates"/> against a model: random resets of random shapes, each followed by adds drawn
        /// from a small or a large key space, so that both the full clear and the entry-by-entry clear run after tables
        /// of every size. Each add must report exactly what a set of keys reports, and the entries must be the first
        /// per key.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void ACandidateArenaMatchesAModelOverRandomResets(int seed)
        {
            var random = new Random(seed);
            var arena = new LiftCandidates();
            for (var round = 0; round < 200; round++)
            {
                var width = random.Next(1, 4);
                var keyWidth = random.Next(0, width + 1);
                arena.Reset(width, keyWidth);
                var model = new Dictionary<string, uint[]>();
                var order = new List<uint[]>();
                var adds = random.Next(4) == 0 ? random.Next(1000, 5000) : random.Next(0, 20);
                var space = (uint)(random.Next(2) == 0 ? 8 : 100_000);
                for (var i = 0; i < adds; i++)
                {
                    var entry = new uint[width];
                    for (var c = 0; c < width; c++)
                        entry[c] = (uint)random.Next((int)space);

                    var key = string.Join(",", entry.Take(keyWidth));
                    var expected = model.ContainsKey(key) == false;
                    if (expected)
                    {
                        model[key] = entry;
                        order.Add(entry);
                    }

                    Assert.Equal(expected, arena.Add(entry));
                }

                Assert.Equal(order.Count, arena.Count);
                for (var i = 0; i < order.Count; i++)
                    Assert.Equal(order[i], arena[i].ToArray());
            }
        }

    }

}
