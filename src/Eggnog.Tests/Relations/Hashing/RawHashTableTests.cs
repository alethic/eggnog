using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests <see cref="RawHashTable{TEntry}"/>: hashbrown's sizing, tombstone and rehash rules, the entry API, and
    /// randomized operation sequences against a <see cref="Dictionary{TKey, TValue}"/> under hash functions from
    /// well mixed to fully colliding.
    /// </summary>
    public class RawHashTableTests
    {

        static readonly HashTestEntryHasher Hasher = default;

        static HashTestEntry Entry(int scheme, int key, int value) => new(HashTestHashes.Hash(scheme, key), key, value);

        static int Find(RawHashTable<HashTestEntry> table, int scheme, int key)
        {
            var match = new HashTestEntryMatch(key);
            return table.Find(HashTestHashes.Hash(scheme, key), ref match);
        }

        static bool Remove(RawHashTable<HashTestEntry> table, int scheme, int key)
        {
            var match = new HashTestEntryMatch(key);
            return table.Remove(HashTestHashes.Hash(scheme, key), ref match, out _);
        }

        static void Insert(RawHashTable<HashTestEntry> table, int scheme, int key, int value)
        {
            var entry = Entry(scheme, key, value);
            table.InsertUnique(entry.Hash, entry, Hasher);
        }

        static int Check(RawHashTable<HashTestEntry> table) => RawHashTableInvariants.Check(table, Hasher);

        [Fact]
        public void EmptyTableAllocatesNothing()
        {
            var table = new RawHashTable<HashTestEntry>();
            Assert.Equal(0, table.Count);
            Assert.True(table.IsEmpty);
            Assert.Equal(0, table.Capacity);
            Assert.Equal(0, table.SlotCount);
            Assert.Equal(-1, Find(table, 0, 1));
            Assert.False(Remove(table, 0, 1));
            Assert.Equal(-1, table.NextFullSlot(0));

            var enumerator = table.GetEnumerator();
            Assert.False(enumerator.MoveNext());

            table.Clear();
            Check(table);

            var zero = new RawHashTable<HashTestEntry>(0);
            Assert.Equal(0, zero.SlotCount);
            Check(zero);
        }

        [Fact]
        public void InsertedEntriesAreFound()
        {
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < 10_000; key++)
                Insert(table, 0, key, key * 3);

            Assert.Equal(10_000, table.Count);
            Check(table);

            for (var key = 0; key < 10_000; key++)
            {
                var slot = Find(table, 0, key);
                Assert.True(slot >= 0);
                Assert.Equal(key * 3, table.GetEntry(slot).Value);
                Assert.True(table.IsFull(slot));
            }

            for (var key = 10_000; key < 20_000; key++)
                Assert.Equal(-1, Find(table, 0, key));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void InsertFindRemoveUnderEveryHashScheme(int scheme)
        {
            var count = scheme == 3 ? 300 : 3000;
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < count; key++)
                Insert(table, scheme, key, key);

            Check(table);
            for (var key = 0; key < count; key++)
                Assert.Equal(key, table.GetEntry(Find(table, scheme, key)).Value);

            for (var key = 0; key < count; key += 2)
                Assert.True(Remove(table, scheme, key));

            Check(table);
            for (var key = 0; key < count; key++)
                Assert.Equal(key % 2 == 1, Find(table, scheme, key) >= 0);

            for (var key = 0; key < count; key += 2)
                Insert(table, scheme, key, -key);

            Check(table);
            for (var key = 0; key < count; key++)
                Assert.Equal(key % 2 == 1 ? key : -key, table.GetEntry(Find(table, scheme, key)).Value);
        }

        [Fact]
        public void CapacityToBucketsMatchesHashbrown()
        {
            var expected = new (int Capacity, int Buckets)[]
            {
                (1, 4), (3, 4), (4, 8), (7, 8), (8, 16), (14, 16), (15, 32), (28, 32), (29, 64), (56, 64), (57, 128),
                (112, 128), (113, 256), (896, 1024), (897, 2048),
            };

            foreach (var (capacity, buckets) in expected)
                Assert.Equal(buckets, RawHashTable<HashTestEntry>.CapacityToBuckets(capacity));

            Assert.Equal(3, RawHashTable<HashTestEntry>.BucketMaskToCapacity(3));
            Assert.Equal(7, RawHashTable<HashTestEntry>.BucketMaskToCapacity(7));
            Assert.Equal(14, RawHashTable<HashTestEntry>.BucketMaskToCapacity(15));
            Assert.Equal(28, RawHashTable<HashTestEntry>.BucketMaskToCapacity(31));
            Assert.Equal(896, RawHashTable<HashTestEntry>.BucketMaskToCapacity(1023));
        }

        [Fact]
        public void GrowthFollowsHashbrownSizes()
        {
            // Each growth goes to max(items + 1, capacity + 1) entries, which doubles the slot count.
            var table = new RawHashTable<HashTestEntry>();
            var expected = new SortedDictionary<int, int> { [1] = 4, [4] = 8, [8] = 16, [15] = 32, [29] = 64, [57] = 128, [113] = 256 };
            var buckets = 0;
            for (var n = 1; n <= 200; n++)
            {
                Insert(table, 0, n, n);
                if (expected.TryGetValue(n, out var grown))
                    buckets = grown;

                Assert.Equal(buckets, table.SlotCount);
                Assert.Equal(RawHashTable<HashTestEntry>.BucketMaskToCapacity(buckets - 1), table.Capacity);
            }

            Check(table);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(14)]
        [InlineData(15)]
        [InlineData(100)]
        [InlineData(1000)]
        [InlineData(4096)]
        public void InitialCapacityHoldsThatManyWithoutGrowth(int capacity)
        {
            var table = new RawHashTable<HashTestEntry>(capacity);
            var slots = table.SlotCount;
            Assert.True(table.Capacity >= capacity);
            for (var key = 0; key < capacity; key++)
                Insert(table, 0, key, key);

            Assert.Equal(slots, table.SlotCount);
            Check(table);
        }

        [Fact]
        public void ReserveMakesRoomUpFront()
        {
            var table = new RawHashTable<HashTestEntry>();
            table.Reserve(1000, Hasher);
            var slots = table.SlotCount;
            Assert.True(table.Capacity >= 1000);
            for (var key = 0; key < 1000; key++)
                Insert(table, 0, key, key);

            Assert.Equal(slots, table.SlotCount);

            // Reserving what is already available changes nothing.
            table.Reserve(table.Capacity - table.Count, Hasher);
            Assert.Equal(slots, table.SlotCount);
            Check(table);
        }

        [Fact]
        public void TablesSmallerThanAGroupNeverHoldTombstones()
        {
            // With 4 or 8 slots, every erase sees the EMPTY padding bytes of the group, so slots go back to EMPTY.
            var table = new RawHashTable<HashTestEntry>(7);
            Assert.Equal(8, table.SlotCount);
            for (var round = 0; round < 50; round++)
            {
                for (var key = 0; key < 7; key++)
                    Insert(table, 3, key, round);

                Assert.Equal(8, table.SlotCount);
                for (var key = 0; key < 7; key++)
                    Assert.True(Remove(table, 3, key));

                Assert.Equal(0, Check(table));
                Assert.Equal(7, table.GrowthLeft);
            }
        }

        [Fact]
        public void RemovalInsideAShortRunFreesTheSlot()
        {
            var table = new RawHashTable<HashTestEntry>(28);
            for (var key = 0; key < 3; key++)
                Insert(table, 3, key, key);

            var growth = table.GrowthLeft;
            var slot = Find(table, 3, 1);
            Assert.True(Remove(table, 3, 1));
            Assert.Equal(ControlGroup.Empty, table.Control[slot]);
            Assert.Equal(growth + 1, table.GrowthLeft);
            Assert.Equal(0, Check(table));
        }

        [Fact]
        public void RemovalInsideALongRunLeavesATombstoneThatProbesSkip()
        {
            // 20 identical hashes fill a contiguous run of 20 slots; removing from its middle must keep probes going.
            var table = new RawHashTable<HashTestEntry>(28);
            for (var key = 0; key < 20; key++)
                Insert(table, 3, key, key);

            var growth = table.GrowthLeft;
            var slot = Find(table, 3, 5);
            Assert.True(Remove(table, 3, 5));
            Assert.Equal(ControlGroup.Deleted, table.Control[slot]);
            Assert.Equal(growth, table.GrowthLeft);
            Assert.Equal(1, Check(table));

            for (var key = 0; key < 20; key++)
                Assert.Equal(key != 5, Find(table, 3, key) >= 0);

            // A new entry reuses the tombstone, which costs no growth.
            Insert(table, 3, 100, 100);
            Assert.Equal(slot, Find(table, 3, 100));
            Assert.Equal(growth, table.GrowthLeft);
            Assert.Equal(0, Check(table));
        }

        [Fact]
        public void InsertAtTheLoadLimitRehashesTombstonesInPlace()
        {
            // Fill a 32-slot table to capacity with one hash: a run over slots 7..31 and 0..2, leaving 3..6 EMPTY.
            var table = new RawHashTable<HashTestEntry>(28);
            Assert.Equal(32, table.SlotCount);
            for (var key = 0; key < 28; key++)
                Insert(table, 3, key, key);

            Assert.Equal(0, table.GrowthLeft);

            // Remove 15 entries from the middle of the run: each leaves a tombstone, so no growth comes back.
            for (var key = 6; key < 21; key++)
                Assert.True(Remove(table, 3, key));

            Assert.Equal(15, Check(table));
            Assert.Equal(0, table.GrowthLeft);

            // An insert whose probe starts at slot 3 lands on an EMPTY slot with no growth left: 14 live entries are at
            // most half of 28, so the table rehashes in place instead of growing.
            var entry = new HashTestEntry(3, 1000, 1000);
            table.InsertUnique(entry.Hash, entry, Hasher);

            Assert.Equal(32, table.SlotCount);
            Assert.Equal(0, Check(table));
            Assert.Equal(14, table.Count);
            Assert.Equal(28 - 14, table.GrowthLeft);

            var match = new HashTestEntryMatch(1000);
            Assert.True(table.TryFind(3, ref match, out var found));
            Assert.Equal(1000, found.Value);
            for (var key = 0; key < 28; key++)
                Assert.Equal(key < 6 || key >= 21, Find(table, 3, key) >= 0);
        }

        [Fact]
        public void InsertAtTheLoadLimitGrowsWhenMoreThanHalfIsLive()
        {
            var table = new RawHashTable<HashTestEntry>(28);
            for (var key = 0; key < 28; key++)
                Insert(table, 3, key, key);

            // 14 tombstones leave 14 live entries; one more would exceed half the capacity.
            for (var key = 6; key < 20; key++)
                Assert.True(Remove(table, 3, key));

            Assert.Equal(0, table.GrowthLeft);
            var entry = new HashTestEntry(3, 1000, 1000);
            table.InsertUnique(entry.Hash, entry, Hasher);

            Assert.Equal(64, table.SlotCount);
            Assert.Equal(0, Check(table));
            Assert.Equal(15, table.Count);
        }

        [Fact]
        public void ReserveRehashesInPlaceWhenTombstonesHoldTheRoom()
        {
            var table = new RawHashTable<HashTestEntry>(28);
            for (var key = 0; key < 28; key++)
                Insert(table, 3, key, key);

            for (var key = 4; key < 24; key++)
                Assert.True(Remove(table, 3, key));

            Assert.True(Check(table) > 0);

            table.Reserve(6, Hasher);
            Assert.Equal(32, table.SlotCount);
            Assert.Equal(0, Check(table));
            Assert.Equal(28 - 8, table.GrowthLeft);
            for (var key = 0; key < 28; key++)
                Assert.Equal(key < 4 || key >= 24, Find(table, 3, key) >= 0);
        }

        [Fact]
        public void FindOrFindInsertSlotWorksAsAnEntryApi()
        {
            // Count word occurrences, the way egglog's StagedOutputs and TupleIndex use HashTable::entry.
            var random = new Random(7);
            var words = Enumerable.Range(0, 20_000).Select(_ => random.Next(2000)).ToArray();
            var table = new RawHashTable<HashTestEntry>();
            foreach (var word in words)
            {
                var hash = HashTestHashes.Hash(0, word);
                var match = new HashTestEntryMatch(word);
                var result = table.FindOrFindInsertSlot(hash, ref match, Hasher);
                if (result >= 0)
                {
                    ref var entry = ref table.EntryAt(result);
                    entry = entry with { Value = entry.Value + 1 };
                }
                else
                {
                    table.InsertInSlot(hash, ~result, new HashTestEntry(hash, word, 1));
                }
            }

            Check(table);
            var expected = words.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(expected.Count, table.Count);
            foreach (var (word, count) in expected)
                Assert.Equal(count, table.GetEntry(Find(table, 0, word)).Value);
        }

        [Fact]
        public void FindOrFindInsertSlotLooksPastTombstones()
        {
            // The first free slot of the probe is a tombstone, but the entry sits further on: it must be found, not
            // inserted twice.
            var table = new RawHashTable<HashTestEntry>(28);
            for (var key = 0; key < 20; key++)
                Insert(table, 3, key, key);

            Assert.True(Remove(table, 3, 2));
            var hash = HashTestHashes.Hash(3, 15);
            var match = new HashTestEntryMatch(15);
            var result = table.FindOrFindInsertSlot(hash, ref match, Hasher);
            Assert.True(result >= 0);
            Assert.Equal(15, table.GetEntry(result).Key);

            // An absent key gets the tombstone as its insert slot.
            var tombstone = Array.IndexOf(table.Control.ToArray(), ControlGroup.Deleted);
            var absent = new HashTestEntryMatch(99);
            result = table.FindOrFindInsertSlot(hash, ref absent, Hasher);
            Assert.Equal(tombstone, ~result);
            table.InsertInSlot(hash, ~result, new HashTestEntry(hash, 99, 99));
            Assert.Equal(0, Check(table));
            Assert.Equal(tombstone, Find(table, 3, 99));
        }

        [Fact]
        public void FindOrFindInsertSlotReservesBeforeLooking()
        {
            // Like hashbrown's entry(), the lookup grows a full table even when the key is present.
            var table = new RawHashTable<HashTestEntry>(3);
            for (var key = 0; key < 3; key++)
                Insert(table, 0, key, key);

            Assert.Equal(4, table.SlotCount);
            var hash = HashTestHashes.Hash(0, 1);
            var match = new HashTestEntryMatch(1);
            var result = table.FindOrFindInsertSlot(hash, ref match, Hasher);
            Assert.True(result >= 0);
            Assert.Equal(8, table.SlotCount);
            Assert.Equal(1, table.GetEntry(result).Key);
        }

        [Fact]
        public void FindRefAllowsUpdatesInPlace()
        {
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < 100; key++)
                Insert(table, 0, key, key);

            for (var key = 0; key < 100; key++)
            {
                var match = new HashTestEntryMatch(key);
                ref var entry = ref table.FindRef(HashTestHashes.Hash(0, key), ref match, out var found);
                Assert.True(found);
                entry = entry with { Value = entry.Value + 1000 };
            }

            var missing = new HashTestEntryMatch(1000);
            table.FindRef(HashTestHashes.Hash(0, 1000), ref missing, out var absent);
            Assert.False(absent);

            for (var key = 0; key < 100; key++)
                Assert.Equal(key + 1000, table.GetEntry(Find(table, 0, key)).Value);
        }

        [Fact]
        public void TagCollisionsAreResolvedByTheMatch()
        {
            // Scheme 1 gives every key tag 0, so every full byte of a probed group is a candidate.
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < 64; key++)
                Insert(table, 1, key, key);

            // 128 slots; keys 0..63 sit in slots 0..63. An absent key starting at slot 10 checks the groups at 10 and
            // 26 (16 candidates each), then the group at 58 (slots 58..63, then EMPTY slots): 38 comparisons along
            // the triangular probe sequence.
            Assert.Equal(128, table.SlotCount);
            var match = new HashTestEntryMatch(1024 + 10);
            Assert.Equal(-1, table.Find(HashTestHashes.Hash(1, 1024 + 10), ref match));
            Assert.Equal(38, match.Calls);

            for (var key = 0; key < 64; key++)
                Assert.Equal(key, table.GetEntry(Find(table, 1, key)).Value);
        }

        [Fact]
        public void InsertUniqueDoesNotLookForDuplicates()
        {
            var table = new RawHashTable<HashTestEntry>();
            Insert(table, 0, 1, 10);
            Insert(table, 0, 1, 20);
            Assert.Equal(2, table.Count);
            Assert.True(Remove(table, 0, 1));
            Assert.True(Remove(table, 0, 1));
            Assert.False(Remove(table, 0, 1));
            Check(table);
        }

        [Fact]
        public void RetainRemovesRejectedEntriesAndKeepsUpdates()
        {
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < 5000; key++)
                Insert(table, 0, key, key);

            var predicate = new HashTestEntryPredicate(Enumerable.Range(0, 5000).Where(k => k % 3 == 0).ToHashSet(), 7);
            table.Retain(ref predicate);

            Assert.Equal(5000, predicate.Visited);
            Check(table);
            for (var key = 0; key < 5000; key++)
            {
                var slot = Find(table, 0, key);
                if (key % 3 == 0)
                    Assert.Equal(-1, slot);
                else
                    Assert.Equal(key + 7, table.GetEntry(slot).Value);
            }
        }

        [Fact]
        public void ClearKeepsTheAllocation()
        {
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < 1000; key++)
                Insert(table, 0, key, key);

            var slots = table.SlotCount;
            table.Clear();
            Assert.Equal(0, table.Count);
            Assert.Equal(slots, table.SlotCount);
            Assert.Equal(RawHashTable<HashTestEntry>.BucketMaskToCapacity(slots - 1), table.Capacity);
            Check(table);
            for (var key = 0; key < 1000; key++)
                Assert.Equal(-1, Find(table, 0, key));

            for (var key = 0; key < 1000; key++)
                Insert(table, 0, key, -key);

            Assert.Equal(slots, table.SlotCount);
            Check(table);
        }

        [Fact]
        public void ClearResetsTombstonesOfAnEmptiedTable()
        {
            var table = new RawHashTable<HashTestEntry>(28);
            for (var key = 0; key < 20; key++)
                Insert(table, 3, key, key);

            for (var key = 0; key < 20; key++)
                Assert.True(Remove(table, 3, key));

            Assert.True(Check(table) > 0);
            table.Clear();
            Assert.Equal(0, Check(table));
            Assert.Equal(28, table.GrowthLeft);
        }

        [Fact]
        public void CloneIsIndependent()
        {
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < 500; key++)
                Insert(table, 0, key, key);

            var copy = table.Clone();
            Assert.Equal(table.Count, copy.Count);
            Assert.Equal(table.SlotCount, copy.SlotCount);
            Assert.Equal(table.Control.ToArray(), copy.Control.ToArray());

            for (var key = 0; key < 500; key += 2)
                Assert.True(Remove(copy, 0, key));

            Insert(copy, 0, 10_000, 1);
            Assert.Equal(500, table.Count);
            for (var key = 0; key < 500; key++)
                Assert.True(Find(table, 0, key) >= 0);

            Assert.Equal(-1, Find(table, 0, 10_000));
            Check(table);
            Check(copy);

            var empty = new RawHashTable<HashTestEntry>().Clone();
            Insert(empty, 0, 1, 1);
            Assert.Equal(1, empty.Count);
            Check(empty);
        }

        [Fact]
        public void EnumeratorYieldsEveryEntryOnceInSlotOrder()
        {
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < 3000; key++)
                Insert(table, 0, key, key);

            for (var key = 0; key < 3000; key += 5)
                Assert.True(Remove(table, 0, key));

            var keys = new List<int>();
            var lastSlot = -1;
            var enumerator = table.GetEnumerator();
            while (enumerator.MoveNext())
            {
                Assert.True(enumerator.Slot > lastSlot);
                lastSlot = enumerator.Slot;
                Assert.True(table.IsFull(enumerator.Slot));
                keys.Add(enumerator.Current.Key);
            }

            Assert.Equal(Enumerable.Range(0, 3000).Where(k => k % 5 != 0), keys.Order());

            foreach (ref var entry in table)
                entry = entry with { Value = -entry.Key };

            foreach (var entry in table)
                Assert.Equal(-entry.Key, entry.Value);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(14)]
        [InlineData(100)]
        [InlineData(2000)]
        public void NextFullSlotVisitsTheSameSlotsAsTheEnumerator(int count)
        {
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < count; key++)
                Insert(table, 0, key, key);

            var expected = new List<int>();
            var enumerator = table.GetEnumerator();
            while (enumerator.MoveNext())
                expected.Add(enumerator.Slot);

            var actual = new List<int>();
            for (var slot = table.NextFullSlot(0); slot >= 0; slot = table.NextFullSlot(slot + 1))
                actual.Add(slot);

            Assert.Equal(expected, actual);
            Assert.Equal(-1, table.NextFullSlot(table.SlotCount));
            Assert.Equal(-1, table.NextFullSlot(table.SlotCount + 100));
        }

        [Fact]
        public void EnumeratorAllowsRemovingTheCurrentEntry()
        {
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < 3000; key++)
                Insert(table, 0, key, key);

            // hashbrown's RawIter lets the caller erase the item it just yielded: the entries ahead are unchanged.
            var yielded = 0;
            var enumerator = table.GetEnumerator();
            while (enumerator.MoveNext())
            {
                yielded++;
                if (enumerator.Current.Key % 3 == 0)
                    table.RemoveAt(enumerator.Slot);
            }

            Assert.Equal(3000, yielded);
            Assert.Equal(2000, table.Count);
            Check(table);
            for (var key = 0; key < 3000; key++)
                Assert.Equal(key % 3 != 0, Find(table, 0, key) >= 0);
        }

        [Fact]
        public void EnumeratorThrowsWhenEntriesAheadWereRemoved()
        {
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < 100; key++)
                Insert(table, 0, key, key);

            var enumerator = table.GetEnumerator();
            Assert.True(enumerator.MoveNext());
            var current = enumerator.Current.Key;
            for (var key = 0; key < 100; key++)
                if (key != current)
                    Assert.True(Remove(table, 0, key));

            var threw = false;
            try
            {
                while (enumerator.MoveNext())
                {

                }
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw);
        }

        [Fact]
        public void ScansOfSparseTablesVisitEveryEntryOnce()
        {
            // Entries clustered at the start of a large table: the scans stop at the last one, and must still see all.
            var table = new RawHashTable<HashTestEntry>(1 << 14);
            var slots = table.SlotCount;
            for (var key = 0; key < 40; key++)
            {
                var hash = ((ulong)(key % 128) << 57) | (uint)(key % 3);
                table.InsertUnique(hash, new HashTestEntry(hash, key, key), Hasher);
            }

            Assert.Equal(slots, table.SlotCount);
            var yielded = new List<int>();
            foreach (var entry in table)
                yielded.Add(entry.Key);

            Assert.Equal(Enumerable.Range(0, 40), yielded.Order());

            var predicate = new HashTestEntryPredicate(Enumerable.Range(0, 40).Where(k => k % 2 == 0).ToHashSet(), 1);
            table.Retain(ref predicate);
            Assert.Equal(40, predicate.Visited);
            Assert.Equal(20, table.Count);
            Check(table);

            // Growth moves the entries with the same countdown.
            table.Reserve(slots, Hasher);
            Assert.True(table.SlotCount > slots);
            Assert.Equal(20, table.Count);
            Check(table);
            foreach (var entry in table)
                Assert.Equal(1, entry.Key % 2);
        }

        [Fact]
        public void ScansOfAnEmptyLargeTableCostNothing()
        {
            var table = new RawHashTable<HashTestEntry>(1 << 20);
            Assert.Equal(1 << 21, table.SlotCount);

            var predicate = new HashTestEntryPredicate(new HashSet<int>(), 0);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 200; i++)
            {
                table.Retain(ref predicate);
                Assert.Equal(-1, table.NextFullSlot(0));
            }

            watch.Stop();
            Assert.Equal(0, predicate.Visited);
            Assert.True(watch.ElapsedMilliseconds < 50, $"200 scans of an empty table took {watch.ElapsedMilliseconds} ms.");
        }

        [Fact]
        public void InsertInSlotRejectsAnEmptySlotWithoutGrowthButFillsATombstone()
        {
            // 28 entries with one probe start fill slots 0..27 of a 32-slot table, leaving no growth.
            var table = new RawHashTable<HashTestEntry>(28);
            Assert.Equal(32, table.SlotCount);
            for (var key = 0; key < 28; key++)
            {
                var hash = (ulong)key << 57;
                table.InsertUnique(hash, new HashTestEntry(hash, key, key), Hasher);
            }

            Assert.Equal(0, table.GrowthLeft);

            // Slot 5 lies in a run of more than a group of full slots, so removing it leaves a tombstone, which
            // consumes no growth to refill.
            var match = new HashTestEntryMatch(5);
            Assert.Equal(5, table.Find(5UL << 57, ref match));
            table.RemoveAt(5);
            Assert.Equal(ControlGroup.Deleted, table.Control[5]);
            Assert.Equal(0, table.GrowthLeft);

            var empty = table.Control[..table.SlotCount].IndexOf(ControlGroup.Empty);
            Assert.True(empty >= 0);
            var hash100 = 100UL << 57;
            Assert.Throws<InvalidOperationException>(() => table.InsertInSlot(hash100, empty, new HashTestEntry(hash100, 100, 100)));
            Assert.Equal(27, table.Count);
            Assert.Equal(1, Check(table));

            table.InsertInSlot(hash100, 5, new HashTestEntry(hash100, 100, 100));
            Assert.Equal(28, table.Count);
            Assert.Equal(0, table.GrowthLeft);
            Assert.Equal(0, Check(table));
            var found = new HashTestEntryMatch(100);
            Assert.Equal(5, table.Find(hash100, ref found));
        }

        [Fact]
        public void EntriesWithReferencesAreClearedWhenTheyLeave()
        {
            var hasher = default(HashTestRefEntryHasher);
            var table = new RawHashTable<HashTestRefEntry>();
            for (var i = 0; i < 2000; i++)
            {
                var key = "k" + i;
                table.InsertUnique(FxHasher.Hash((uint)i), new HashTestRefEntry(FxHasher.Hash((uint)i), key), hasher);
            }

            for (var i = 0; i < 2000; i += 2)
            {
                var match = new HashTestRefEntryMatch("k" + i);
                Assert.True(table.Remove(FxHasher.Hash((uint)i), ref match, out var removed));
                Assert.Equal("k" + i, removed.Key);
            }

            RawHashTableInvariants.Check(table, hasher);
            AssertVacantSlotsCleared(table);

            for (var i = 1; i < 2000; i += 2)
            {
                var match = new HashTestRefEntryMatch("k" + i);
                Assert.True(table.TryFind(FxHasher.Hash((uint)i), ref match, out var found));
                Assert.Equal("k" + i, found.Key);
            }

            table.Clear();
            AssertVacantSlotsCleared(table);
        }

        [Fact]
        public void InPlaceRehashMovesReferenceEntriesAndClearsVacatedSlots()
        {
            var hasher = default(HashTestRefEntryHasher);
            var table = new RawHashTable<HashTestRefEntry>(28);
            for (var i = 0; i < 28; i++)
                table.InsertUnique(HashTestHashes.Hash(2, i), new HashTestRefEntry(HashTestHashes.Hash(2, i), "k" + i), hasher);

            for (var i = 4; i < 24; i++)
            {
                var match = new HashTestRefEntryMatch("k" + i);
                Assert.True(table.Remove(HashTestHashes.Hash(2, i), ref match, out _));
            }

            table.Reserve(6, hasher);
            Assert.Equal(32, table.SlotCount);
            Assert.Equal(0, RawHashTableInvariants.Check(table, hasher));
            AssertVacantSlotsCleared(table);

            for (var i = 0; i < 28; i++)
            {
                var match = new HashTestRefEntryMatch("k" + i);
                Assert.Equal(i < 4 || i >= 24, table.TryFind(HashTestHashes.Hash(2, i), ref match, out _));
            }
        }

        static void AssertVacantSlotsCleared(RawHashTable<HashTestRefEntry> table)
        {
            var control = table.Control;
            var entries = table.RawEntries;
            for (var i = 0; i < table.SlotCount; i++)
                if (ControlGroup.IsFull(control[i]) == false)
                    Assert.Null(entries[i].Key);
        }

        [Fact]
        public void InvalidArgumentsThrow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RawHashTable<HashTestEntry>(-1));

            var table = new RawHashTable<HashTestEntry>();
            Assert.Throws<ArgumentOutOfRangeException>(() => table.Reserve(-1, Hasher));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.RemoveAt(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.NextFullSlot(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.IsFull(0));

            Insert(table, 0, 1, 1);
            var full = Find(table, 0, 1);
            var vacant = full == 0 ? 1 : 0;
            Assert.Throws<ArgumentOutOfRangeException>(() => table.RemoveAt(vacant));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.RemoveAt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.RemoveAt(table.SlotCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.InsertInSlot(0, full, default));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.InsertInSlot(0, table.SlotCount, default));

            table.RemoveAt(full);
            Assert.Equal(0, table.Count);
            Check(table);
        }

        [Fact]
        public void ManyEntriesRoundTrip()
        {
            const int count = 1 << 20;
            var table = new RawHashTable<HashTestEntry>();
            for (var key = 0; key < count; key++)
                Insert(table, 0, key, key);

            Assert.Equal(count, table.Count);
            Assert.Equal(1 << 21, table.SlotCount);
            Check(table);

            for (var key = 0; key < count; key++)
                Assert.True(Remove(table, 0, key));

            Assert.True(table.IsEmpty);
            Check(table);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void RandomOperationsMatchADictionary(int scheme)
        {
            var random = new Random(100 + scheme);
            var keySpace = scheme == 3 ? 200 : 4000;
            var operations = scheme == 3 ? 30_000 : 200_000;
            var table = new RawHashTable<HashTestEntry>();
            var reference = new Dictionary<int, int>();

            for (var op = 0; op < operations; op++)
            {
                var key = random.Next(keySpace);
                var hash = HashTestHashes.Hash(scheme, key);
                var match = new HashTestEntryMatch(key);
                var roll = random.Next(1000);

                if (roll < 400)
                {
                    // Upsert through the entry API.
                    var result = table.FindOrFindInsertSlot(hash, ref match, Hasher);
                    Assert.Equal(reference.ContainsKey(key), result >= 0);
                    if (result >= 0)
                        table.EntryAt(result) = new HashTestEntry(hash, key, op);
                    else
                        table.InsertInSlot(hash, ~result, new HashTestEntry(hash, key, op));

                    reference[key] = op;
                }
                else if (roll < 550)
                {
                    if (reference.ContainsKey(key) == false)
                    {
                        table.InsertUnique(hash, new HashTestEntry(hash, key, op), Hasher);
                        reference[key] = op;
                    }
                }
                else if (roll < 850)
                {
                    var removed = table.Remove(hash, ref match, out var entry);
                    Assert.Equal(reference.Remove(key, out var value), removed);
                    if (removed)
                        Assert.Equal(value, entry.Value);
                }
                else if (roll < 990)
                {
                    var found = table.TryFind(hash, ref match, out var entry);
                    Assert.Equal(reference.TryGetValue(key, out var value), found);
                    if (found)
                        Assert.Equal(value, entry.Value);
                }
                else if (roll < 995)
                {
                    var remove = reference.Keys.Where(_ => random.Next(10) == 0).ToHashSet();
                    var predicate = new HashTestEntryPredicate(remove, 0);
                    table.Retain(ref predicate);
                    foreach (var k in remove)
                        reference.Remove(k);
                }
                else if (roll < 998)
                {
                    table.Reserve(random.Next(200), Hasher);
                }
                else if (random.Next(10) == 0)
                {
                    table.Clear();
                    reference.Clear();
                }

                Assert.Equal(reference.Count, table.Count);
                if (op % 1009 == 0)
                    Check(table);
            }

            Check(table);
            foreach (var (key, value) in reference)
                Assert.Equal(value, table.GetEntry(Find(table, scheme, key)).Value);

            var enumerated = new Dictionary<int, int>();
            foreach (var entry in table)
                enumerated.Add(entry.Key, entry.Value);

            Assert.Equal(reference.OrderBy(p => p.Key), enumerated.OrderBy(p => p.Key));
        }

    }

}
