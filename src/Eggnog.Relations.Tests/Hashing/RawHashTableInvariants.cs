using System.Numerics;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Checks the structural invariants of a <see cref="RawHashTable{TEntry}"/> from its control bytes.
    /// </summary>
    internal static class RawHashTableInvariants
    {

        /// <summary>
        /// Asserts every structural invariant: power-of-two slot count, consistent mirror group, tags equal to the
        /// stored entries' hashes, item count, and growth accounting (<c>growth + items + tombstones == capacity</c>).
        /// </summary>
        /// <typeparam name="TEntry">The entry type.</typeparam>
        /// <typeparam name="THasher">The hasher type.</typeparam>
        /// <param name="table">The table.</param>
        /// <param name="hasher">Gets each entry's hash.</param>
        /// <returns>The number of tombstones.</returns>
        public static int Check<TEntry, THasher>(RawHashTable<TEntry> table, THasher hasher)
            where TEntry : struct
            where THasher : struct, IEntryHasher<TEntry>
        {
            var control = table.Control;
            var buckets = table.SlotCount;
            const int width = ControlGroup.Width;

            if (buckets == 0)
            {
                Assert.Equal(0, table.Count);
                Assert.Equal(0, table.GrowthLeft);
                Assert.Equal(width, control.Length);
                foreach (var b in control)
                    Assert.Equal(ControlGroup.Empty, b);

                return 0;
            }

            Assert.True(BitOperations.IsPow2(buckets));
            Assert.Equal(buckets + width, control.Length);

            var full = 0;
            var deleted = 0;
            var entries = table.RawEntries;
            for (var i = 0; i < buckets; i++)
            {
                var b = control[i];
                if (b == ControlGroup.Empty)
                    continue;

                if (b == ControlGroup.Deleted)
                {
                    deleted++;
                    continue;
                }

                Assert.True(b < 0x80, $"Slot {i} has invalid control byte {b:X2}.");
                Assert.Equal(ControlGroup.Tag(hasher.Hash(in entries[i])), b);
                full++;
            }

            Assert.Equal(table.Count, full);
            Assert.Equal(RawHashTable<TEntry>.BucketMaskToCapacity(buckets - 1), table.GrowthLeft + full + deleted);
            Assert.True(table.GrowthLeft >= 0);

            if (buckets < width)
            {
                Assert.Equal(0, deleted);
                for (var i = buckets; i < width; i++)
                    Assert.Equal(ControlGroup.Empty, control[i]);

                for (var i = 0; i < buckets; i++)
                    Assert.Equal(control[i], control[width + i]);
            }
            else
            {
                for (var i = 0; i < width; i++)
                    Assert.Equal(control[i], control[buckets + i]);
            }

            return deleted;
        }

    }

}
