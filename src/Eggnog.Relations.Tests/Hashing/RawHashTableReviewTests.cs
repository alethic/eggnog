using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial review tests for <see cref="RawHashTable{TEntry}"/>: invariants after every single operation under
    /// clustered hashes, and the validation the two-call entry API claims to perform.
    /// </summary>
    public class RawHashTableReviewTests
    {

        static readonly HashTestEntryHasher Hasher = default;

        /// <summary>
        /// Clustered hashes: random tags, but only <paramref name="starts"/> distinct probe starts, so runs of
        /// non-empty slots grow long and removals leave tombstones in large tables.
        /// </summary>
        static ulong Clustered(int key, int starts) => (FxHasher.Hash((uint)key) & 0xFFFF_0000_0000_0000UL) | (uint)(key % starts * 5);

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 3)]
        [InlineData(3, 7)]
        [InlineData(4, 64)]
        public void EveryOperationKeepsTheInvariantsUnderClusteredHashes(int seed, int starts)
        {
            var random = new Random(seed);
            var table = new RawHashTable<HashTestEntry>();
            var reference = new Dictionary<int, int>();
            const int keySpace = 600;

            for (var op = 0; op < 40_000; op++)
            {
                var key = random.Next(keySpace);
                var hash = Clustered(key, starts);
                var match = new HashTestEntryMatch(key);
                var roll = random.Next(1000);

                if (roll < 450)
                {
                    var result = table.FindOrFindInsertSlot(hash, ref match, Hasher);
                    Assert.Equal(reference.ContainsKey(key), result >= 0);
                    if (result >= 0)
                        table.EntryAt(result) = new HashTestEntry(hash, key, op);
                    else
                        table.InsertInSlot(hash, ~result, new HashTestEntry(hash, key, op));

                    reference[key] = op;
                }
                else if (roll < 900)
                {
                    var removed = table.Remove(hash, ref match, out var entry);
                    Assert.Equal(reference.Remove(key, out var value), removed);
                    if (removed)
                        Assert.Equal(value, entry.Value);
                }
                else if (roll < 950)
                {
                    // Remove through a slot found by the external cursor.
                    var slot = table.NextFullSlot(random.Next(Math.Max(1, table.SlotCount)));
                    if (slot >= 0)
                    {
                        var victim = table.GetEntry(slot);
                        table.RemoveAt(slot);
                        Assert.True(reference.Remove(victim.Key));
                    }
                }
                else if (roll < 970)
                {
                    var remove = reference.Keys.Where(_ => random.Next(4) == 0).ToHashSet();
                    var predicate = new HashTestEntryPredicate(remove, 1);
                    table.Retain(ref predicate);
                    foreach (var k in reference.Keys.ToList())
                    {
                        if (remove.Contains(k))
                            reference.Remove(k);
                        else
                            reference[k]++;
                    }
                }
                else if (roll < 990)
                {
                    table.Reserve(random.Next(64), Hasher);
                }
                else if (roll < 995)
                {
                    var copy = table.Clone();
                    RawHashTableInvariants.Check(copy, Hasher);
                    Assert.Equal(table.Count, copy.Count);
                }
                else
                {
                    table.Clear();
                    reference.Clear();
                }

                Assert.Equal(reference.Count, table.Count);
                RawHashTableInvariants.Check(table, Hasher);
            }

            var cursor = 0;
            var visited = 0;
            for (var slot = table.NextFullSlot(0); slot >= 0; slot = slot + 1 < table.SlotCount ? table.NextFullSlot(slot + 1) : -1)
            {
                Assert.True(slot >= cursor);
                cursor = slot + 1;
                visited++;
                var entry = table.GetEntry(slot);
                Assert.Equal(reference[entry.Key], entry.Value);
            }

            Assert.Equal(reference.Count, visited);
        }

        /// <summary>
        /// <see cref="RawHashTable{TEntry}.InsertInSlot"/> claims to validate its slot instead of relying on
        /// hashbrown's unsafe precondition, but it accepts an EMPTY slot when no growth is left. That drives
        /// <c>growthLeft</c> negative, after which <see cref="RawHashTable{TEntry}.InsertUnique{THasher}"/> no longer
        /// grows (it tests <c>growthLeft == 0</c>) and fills the last EMPTY slot, so any later probe for an absent key
        /// never terminates. hashbrown's <c>VacantEntry</c> makes this state unreachable.
        /// </summary>
        [Fact]
        public void InsertInSlotRejectsAnEmptySlotWhenNoGrowthIsLeft()
        {
            var table = new RawHashTable<HashTestEntry>(14);
            Assert.Equal(16, table.SlotCount);
            for (var key = 0; key < 14; key++)
            {
                var h = FxHasher.Hash((uint)key);
                table.InsertUnique(h, new HashTestEntry(h, key, key), Hasher);
            }

            Assert.Equal(0, table.GrowthLeft);
            var empty = table.Control[..table.SlotCount].IndexOf(ControlGroup.Empty);
            Assert.True(empty >= 0);

            var hash = FxHasher.Hash(1000u);
            var threw = false;
            try
            {
                table.InsertInSlot(hash, empty, new HashTestEntry(hash, 1000, 1000));
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Assert.True(threw || table.GrowthLeft >= 0, $"InsertInSlot accepted an EMPTY slot with no growth left; growthLeft is now {table.GrowthLeft}.");
        }

        /// <summary>
        /// hashbrown's <c>RawIter</c> (behind <c>iter</c>, <c>iter_mut</c>, <c>retain</c> and <c>drain</c>) and
        /// <c>FullBucketsIndices</c> (behind <c>resize</c>) count down the items still to yield and stop at zero, so
        /// iterating an empty table is O(1) and iterating a sparse one stops at its last entry. The port's enumerator,
        /// <c>Retain</c> and <c>Resize</c> always scan every group, so iterating an empty table of 2^21 slots loads
        /// 2^17 groups each time.
        /// </summary>
        [Fact]
        public void EnumeratingAnEmptyTableStopsOnceEveryItemWasYielded()
        {
            var table = new RawHashTable<uint>(1 << 20);
            Assert.Equal(1 << 21, table.SlotCount);
            Assert.Equal(0, table.Count);

            var seen = 0;
            foreach (ref var entry in table)
                seen++;

            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++)
            {
                foreach (ref var entry in table)
                    seen++;
            }

            watch.Stop();
            Assert.Equal(0, seen);
            Assert.True(watch.ElapsedMilliseconds < 50, $"200 enumerations of an empty table took {watch.ElapsedMilliseconds} ms: each one scanned all {table.SlotCount / ControlGroup.Width} groups.");
        }

    }

}
