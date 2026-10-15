using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ParallelVecWriter{T}"/>: ports of egglog's concurrency/src/tests.rs:358-489, plus
    /// concurrent stress with growth, readers and in-place updates.
    /// </summary>
    public sealed class ParallelVecWriterTests
    {

        [Fact]
        public void BasicParallelWrite()
        {
            const int Threads = 10;
            const int PerThread = 10;
            var finish = new Notification();
            var v = new ParallelVecWriter<int>(Enumerable.Range(0, 100).ToArray());
            var threads = ConcurrencyTestThread.StartMany(Threads, i =>
            {
                var dst = v.WriteContents(Enumerable.Range(0, PerThread).Select(j => i * PerThread + j + 100).ToArray());
                Assert.Equal(0, dst % 10);
                finish.Wait();
            });

            Thread.Sleep(100);
            for (var i = 0; i < 100; i++)
                Assert.Equal(i, v.WithIndex(i, x => x));

            Assert.True(v.WithSlice(0..100, x => x.SequenceEqual(Enumerable.Range(0, 100).ToArray())));
            finish.Notify();
            ConcurrencyTestThread.JoinAll(threads);

            var result = v.Finish(out var count);
            Assert.Equal(200, count);
            var sorted = result[..count];
            Array.Sort(sorted);
            Assert.Equal(Enumerable.Range(0, 200), sorted);
        }

        [Fact]
        public void BasicParallelWriteSlice()
        {
            const int Threads = 10;
            const int PerThread = 10;
            var finish = new Notification();
            var v = new ParallelVecWriter<int>(Enumerable.Range(0, 100).ToArray());
            var threads = ConcurrencyTestThread.StartMany(Threads, i =>
            {
                var items = Enumerable.Range(0, PerThread).Select(j => i * PerThread + j + 100).ToArray();
                var dst = v.WriteSlice(items);
                Assert.Equal(0, dst % PerThread);
                finish.Wait();
            });

            Thread.Sleep(100);
            for (var i = 0; i < 100; i++)
                Assert.Equal(i, v.WithIndex(i, x => x));

            Assert.True(v.WithSlice(0..100, x => x.SequenceEqual(Enumerable.Range(0, 100).ToArray())));
            finish.Notify();
            ConcurrencyTestThread.JoinAll(threads);

            var result = v.Finish(out var count);
            var sorted = result[..count];
            Array.Sort(sorted);
            Assert.Equal(Enumerable.Range(0, 200), sorted);
        }

        [Fact]
        public void UnsafeReadAccessReadsWrittenRanges()
        {
            var v = new ParallelVecWriter<int>([1, 2]);
            var start = v.WriteContents(new[] { 3, 4 });
            using (var access = v.ReadAccess())
            {
                Assert.Equal(1, access.Get(0));
                Assert.Equal(new[] { 3, 4 }, access.GetSlice(start, 2).ToArray());
                Assert.Equal(new[] { 1, 2 }, access.Prefix.ToArray());
                Assert.Equal(2, access.PrefixLength);
            }

            var result = v.Finish(out var count);
            Assert.Equal(new[] { 1, 2, 3, 4 }, result[..count]);
        }

        [Fact]
        public void TakeResetsWriter()
        {
            var v = new ParallelVecWriter<int>([1]);
            v.WriteContents(new[] { 2, 3 });
            var taken = v.Take(out var takenCount);
            Assert.Equal(new[] { 1, 2, 3 }, taken[..takenCount]);

            v.WriteContents(new[] { 4 });
            var finished = v.Finish(out var finishedCount);
            Assert.Equal(new[] { 4 }, finished[..finishedCount]);
        }

        [Fact]
        public void WritesCellSlices()
        {
            // egglog's parallel_vec_writer_writes_cell_slices: write_cell_slice is WriteSlice here.
            var v = new ParallelVecWriter<uint>([1u]);
            var source = new uint[] { 0, 2, 3, 0 };
            Assert.Equal(1, v.WriteSlice(source.AsSpan(1, 2)));
            var result = v.Finish(out var count);
            Assert.Equal(new uint[] { 1, 2, 3 }, result[..count]);
        }

        [Fact]
        public void ThrowsOnCollectionYieldingTooFewItems()
        {
            var v = new ParallelVecWriter<int>(Array.Empty<int>());
            var e = Assert.Throws<InvalidOperationException>(() => v.WriteContents(new ParallelVecWriterLyingCollection(2, 1)));
            Assert.Contains("incorrect number of items", e.Message);
        }

        [Fact]
        public void ThrowsOnCollectionYieldingTooManyItemsWithoutOverrunningItsRange()
        {
            // The Rust writes the surplus past its reservation before panicking; here it must stop at the reservation.
            var v = new ParallelVecWriter<int>(new int[8], 0);
            var e = Assert.Throws<InvalidOperationException>(() => v.WriteContents(new ParallelVecWriterLyingCollection(1, 7, 8, 9)));
            Assert.Contains("incorrect number of items", e.Message);
            var next = v.WriteContents(new[] { 5 });
            Assert.Equal(1, next);
            var result = v.Finish(out var count);
            Assert.Equal(new[] { 7, 5 }, result[..count]);
            Assert.Equal(0, result[2]);
        }

        [Fact]
        public void SpareCapacityIsUsedBeforeGrowing()
        {
            var buffer = new int[16];
            buffer[0] = 1;
            var v = new ParallelVecWriter<int>(buffer, 1);
            Assert.Equal(1, v.WriteSlice(new[] { 2, 3, 4 }));
            var result = v.Finish(out var count);
            Assert.Same(buffer, result);
            Assert.Equal(new[] { 1, 2, 3, 4 }, result[..count]);
        }

        [Fact]
        public void EmptyWritesReturnTheCurrentEnd()
        {
            var v = new ParallelVecWriter<int>([1, 2, 3]);
            Assert.Equal(3, v.WriteSlice(ReadOnlySpan<int>.Empty));
            Assert.Equal(3, v.WriteContents(Array.Empty<int>()));
            v.Finish(out var count);
            Assert.Equal(3, count);
        }

        [Fact]
        public void RejectsBadArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new ParallelVecWriter<int>(null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelVecWriter<int>(new int[2], 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelVecWriter<int>(new int[2], -1));

            // A covariant array would make every span over the storage throw; it is rejected up front.
            Assert.Throws<ArgumentException>(() => new ParallelVecWriter<object>(new string[2], 0));

            // Bad indexes at the API edge are argument errors, not IndexOutOfRangeException.
            var v = new ParallelVecWriter<int>([1, 2]);
            Assert.Throws<ArgumentOutOfRangeException>(() => v.WithIndex(2, x => x));
            Assert.Throws<ArgumentOutOfRangeException>(() => v.WithIndex(-1, x => x));
            Assert.Throws<ArgumentOutOfRangeException>(() => v.WithSlice(1..3, x => x.Length));
            var enumerator = new List<int>().GetEnumerator();
            Assert.Throws<ArgumentOutOfRangeException>(() => v.WriteContents(ref enumerator, -1));
            using (var access = v.ReadAccess())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => access.Get(2));
                Assert.Throws<ArgumentOutOfRangeException>(() => access.Get(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => access.GetSlice(1, 2));
                Assert.Throws<ArgumentOutOfRangeException>(() => access.GetSlice(-1, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => access.GetSlice(0, -1));
                Assert.Equal(0, access.GetSlice(2, 0).Length);
                Assert.Equal(2, access.Get(1));
            }

            // The rejected calls reserved nothing.
            Assert.Equal(2, v.WriteSlice(ReadOnlySpan<int>.Empty));
        }

        [Fact]
        public void WritesFromListsAndStructEnumerators()
        {
            var v = new ParallelVecWriter<int>([1]);
            Assert.Equal(1, v.WriteContents(new List<int> { 2, 3 }));
            Assert.Equal(3, v.WriteContents((IReadOnlyCollection<int>)new List<int> { 4 }));
            Assert.Equal(4, v.WriteContents((IReadOnlyCollection<int>)new[] { 5, 6 }));
            var source = new List<int> { 7, 8, 9 };
            var enumerator = source.GetEnumerator();
            Assert.Equal(6, v.WriteContents(ref enumerator, source.Count));
            var result = v.Finish(out var count);
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, result[..count]);
        }

        [Fact]
        public void StructEnumeratorWithTheWrongCountThrowsWithoutOverrunningItsRange()
        {
            var v = new ParallelVecWriter<int>(new int[8], 0);
            var source = new List<int> { 7, 8, 9 };

            // Claims 1, yields 3: stops after slot 0.
            var tooMany = source.GetEnumerator();
            Assert.Throws<InvalidOperationException>(() => v.WriteContents(ref tooMany, 1));

            // Claims 5, yields 3: [1, 6) stays reserved, its last two slots unwritten.
            var tooFew = source.GetEnumerator();
            Assert.Throws<InvalidOperationException>(() => v.WriteContents(ref tooFew, 5));

            Assert.Equal(6, v.WriteSlice(new[] { 5 }));
            var result = v.Finish(out var count);
            Assert.Equal(new[] { 7, 7, 8, 9, 0, 0, 5 }, result[..count]);
        }

        [Fact]
        public void ThrowingCollectionReleasesItsReadLock()
        {
            // If the enumerator throws mid-write, the read lock must still be released; otherwise the next growth,
            // which waits for every read lock, would wait forever.
            var v = new ParallelVecWriter<int>(new int[4], 0);
            Assert.Throws<NotSupportedException>(() => v.WriteContents(new ParallelVecWriterThrowingCollection(3, 2)));

            var done = new Notification();
            var grower = ConcurrencyTestThread.Start(() =>
            {
                Assert.Equal(3, v.WriteSlice(new int[64]));
                done.Notify();
            });
            Assert.True(done.WaitWithTimeout(TimeSpan.FromSeconds(30)), "growth waited on a leaked read lock");
            grower.Join();

            var result = v.Finish(out var count);
            Assert.Equal(67, count);
            Assert.Equal(new[] { 1, 2, 0 }, result[..3]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void ConcurrentWritersGrowReadersAndOwnersUpdateInPlace(int seed)
        {
            // Writers append random-sized batches from a tiny buffer, forcing many reallocations. Meanwhile "owner"
            // threads overwrite their own prefix cells in place through read access (core-relations'
            // set_stale_shared), and readers check the prefix. No write, batch or in-place update may be lost.
            const int Writers = 6;
            const int Owners = 2;
            const int PrefixLength = 64;
            const int BatchesPerWriter = 300;
            var prefix = Enumerable.Range(0, PrefixLength).Select(i => (long)i).ToArray();
            var v = new ParallelVecWriter<long>(prefix, PrefixLength);
            var batches = new List<(int Start, long[] Items)>[Writers];
            var writersDone = 0;

            var writerThreads = ConcurrencyTestThread.StartMany(Writers, w =>
            {
                var random = new Random(seed * 100 + w);
                var mine = new List<(int, long[])>();
                for (var b = 0; b < BatchesPerWriter; b++)
                {
                    var items = new long[random.Next(0, 40)];
                    for (var i = 0; i < items.Length; i++)
                        items[i] = (long)(w + 1) << 40 | (long)b << 16 | (uint)i;

                    var start = random.Next(2) == 0 ? v.WriteSlice(items) : v.WriteContents(items);
                    mine.Add((start, items));

                    // A completed write is readable through read access.
                    if (items.Length > 0 && random.Next(8) == 0)
                    {
                        using var access = v.ReadAccess();
                        Assert.Equal(items, access.GetSlice(start, items.Length).ToArray());
                    }
                }

                batches[w] = mine;
                Interlocked.Increment(ref writersDone);
            });

            var ownerThreads = ConcurrencyTestThread.StartMany(Owners, o =>
            {
                // Owner o owns prefix cells with index % Owners == o and negates each one, many times over.
                var round = 0;
                while (Volatile.Read(ref writersDone) < Writers || round < 3)
                {
                    using var access = v.ReadAccess();
                    for (var i = o; i < PrefixLength; i += Owners)
                        access.Buffer[i] = -access.Buffer[i] - 1;

                    round++;
                }

                // Leave an even number of flips: each cell back to its original value, then mark it.
                using var final = v.ReadAccess();
                for (var i = o; i < PrefixLength; i += Owners)
                {
                    var value = final.Buffer[i];
                    final.Buffer[i] = (value < 0 ? -value - 1 : value) + 1_000_000;
                }
            });

            var readerThreads = ConcurrencyTestThread.StartMany(2, _ =>
            {
                while (Volatile.Read(ref writersDone) < Writers)
                {
                    using var access = v.ReadAccess();
                    var p = access.Prefix;
                    Assert.Equal(PrefixLength, p.Length);
                    for (var i = 0; i < p.Length; i++)
                    {
                        var x = p[i];
                        var original = x >= 1_000_000 ? x - 1_000_000 : x < 0 ? -x - 1 : x;
                        Assert.Equal(i, original);
                    }
                }
            });

            ConcurrencyTestThread.JoinAll(writerThreads);
            ConcurrencyTestThread.JoinAll(ownerThreads);
            ConcurrencyTestThread.JoinAll(readerThreads);

            var result = v.Finish(out var count);
            var expectedCount = PrefixLength + batches.Sum(list => list.Sum(b => b.Items.Length));
            Assert.Equal(expectedCount, count);
            for (var i = 0; i < PrefixLength; i++)
                Assert.Equal(i + 1_000_000, result[i]);

            var covered = new bool[count];
            foreach (var (start, items) in batches.SelectMany(x => x))
            {
                for (var i = 0; i < items.Length; i++)
                {
                    Assert.False(covered[start + i]);
                    covered[start + i] = true;
                    Assert.Equal(items[i], result[start + i]);
                }
            }

            for (var i = PrefixLength; i < count; i++)
                Assert.True(covered[i]);
        }

        [Fact]
        public void NegativeCountIsRejectedWithoutCorruptingReservations()
        {
            // Rust's ExactSizeIterator::len is a usize, so it can never be negative. IReadOnlyCollection<T>.Count can
            // be. A negative Count is rejected before anything is reserved: added to the logical length
            // (Interlocked.Add), it would move the length backwards, and the loop would write every yielded item past
            // its reservation, over other writers' ranges.
            var v = new ParallelVecWriter<int>(new int[8], 0);
            Assert.Equal(0, v.WriteSlice(new[] { 1, 2 }));

            Assert.ThrowsAny<Exception>(() => v.WriteContents(new ParallelVecWriterLyingCollection(-1, 7, 8, 9)));

            // A rejected write must not shrink the logical length: the next write must not overlap [0, 2).
            var next = v.WriteSlice(new[] { 5 });
            Assert.True(next >= 2, $"the next write landed at {next}, inside the earlier write's range [0, 2)");

            var result = v.Finish(out var count);
            Assert.True(count >= 3, $"the logical length shrank to {count}");
            Assert.Equal(1, result[0]);
            Assert.Equal(2, result[1]);
        }

        [Fact]
        public void NegativeCountNeverWritesOutsideAnyReservation()
        {
            // Slots past the logical length are untouched by a rejected write.
            var v = new ParallelVecWriter<int>(new int[8], 0);
            Assert.Equal(0, v.WriteSlice(new[] { 1 }));
            Assert.ThrowsAny<Exception>(() => v.WriteContents(new ParallelVecWriterLyingCollection(-1, 7, 8, 9)));
            using var access = v.ReadAccess();
            Assert.Equal(1, access.Buffer[0]);
            for (var i = 1; i < access.Buffer.Length; i++)
                Assert.True(access.Buffer[i] == 0, $"slot {i} past every reservation was written ({access.Buffer[i]})");
        }

    }

}
