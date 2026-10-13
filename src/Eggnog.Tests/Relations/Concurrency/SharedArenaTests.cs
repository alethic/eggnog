using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="SharedArena"/>: ports of egglog's concurrency/src/tests.rs:640-788 (the two proptests
    /// become seeded randomized tests) and of the doc examples in shared_arena.rs.
    /// </summary>
    public sealed class SharedArenaTests
    {

        [Fact]
        public void RefsOutliveHandles()
        {
            using var arena = new SharedArena();
            SharedRef<int> value;
            SharedRef<string> text;

            {
                var handle = arena.NewHandle();
                value = handle.Alloc(42);
                text = handle.Alloc("arena allocated");
                Assert.Equal(42, value.Value);
                Assert.Equal("arena allocated", text.Value);
            }

            Assert.Equal(42, value.Value);
            Assert.Equal("arena allocated", text.Value);
        }

        [Fact]
        public void AllocatesFromScopedThreads()
        {
            const int Threads = 8;
            const int PerThread = 64;
            using var arena = new SharedArena();
            var refs = new List<SharedRef<int>>();
            var threads = ConcurrencyTestThread.StartMany(Threads, threadId =>
            {
                var handle = arena.NewHandle();
                for (var offset = 0; offset < PerThread; offset++)
                {
                    var r = handle.Alloc(threadId * PerThread + offset);
                    lock (refs)
                        refs.Add(r);
                }
            });
            ConcurrencyTestThread.JoinAll(threads);

            var values = refs.Select(r => r.Value).OrderBy(x => x).ToArray();
            Assert.Equal(Enumerable.Range(0, Threads * PerThread), values);
        }

        [Fact]
        public void DisposesValuesLifoWithinAHandle()
        {
            var drops = new List<int>();
            using (var arena = new SharedArena())
            {
                var handle = arena.NewHandle();
                for (var value = 0; value < 4; value++)
                    handle.Alloc(new SharedArenaDisposeRecorder(value, drops));

                Assert.Empty(drops);
            }

            Assert.Equal(new[] { 3, 2, 1, 0 }, drops);
        }

        [Fact]
        public void DisposesReferenceTypesAndSkipsOthers()
        {
            var stream = new MemoryStream();
            var arena = new SharedArena();
            var handle = arena.NewHandle();
            var shared = handle.Alloc(stream);
            handle.Alloc("not disposable");
            Assert.True(shared.Value.CanRead);
            arena.Dispose();
            Assert.False(stream.CanRead);

            // Disposing twice is harmless; allocating afterwards is not allowed.
            arena.Dispose();
            Assert.Throws<ObjectDisposedException>(() => handle.Alloc(1));
            Assert.Throws<ObjectDisposedException>(() => arena.NewHandle());
        }

        [Fact]
        public void RefsAreReadableFromOtherThreads()
        {
            // egglog's shared_arena_send_sync_bounds: the arena and its refs may be shared across threads.
            using var arena = new SharedArena();
            var handle = arena.NewHandle();
            var value = handle.Alloc(123L);
            var text = handle.Alloc("shared");
            var reader = ConcurrencyTestThread.Start(() =>
            {
                Assert.Equal(123L, value.Value);
                Assert.Equal("shared", text.Value);
                var own = arena.NewHandle();
                Assert.Equal(7, own.Alloc(7).Value);
            });
            reader.Join();
        }

        [Fact]
        public void HandlesAreBoundToTheirThread()
        {
            // egglog's Handle is !Send (a compile_fail doc test); here using it from another thread throws.
            using var arena = new SharedArena();
            var handle = arena.NewHandle();
            var other = ConcurrencyTestThread.Start(() => Assert.Throws<InvalidOperationException>(() => handle.Alloc(1)));
            other.Join();
        }

        [Fact]
        public void DefaultAndCopyRef()
        {
            using var arena = new SharedArena();
            var handle = arena.NewHandle();
            var value = handle.Alloc(11);
            var copy = value;
            Assert.Equal(11, copy.Value);
            Assert.Equal(value.Value, copy.Value);
            Assert.Equal("11", copy.ToString());
        }

        [Fact]
        public void ValueRefReadsAValueTypeInPlace()
        {
            // The Rust's SharedRef derefs to &T with no copy; ValueRef is that reference, into the arena's box.
            using var arena = new SharedArena();
            var handle = arena.NewHandle();
            var big = handle.Alloc((1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L));
            var copy = big;
            ref readonly var a = ref big.ValueRef();
            ref readonly var b = ref copy.ValueRef();
            Assert.True(Unsafe.AreSame(ref Unsafe.AsRef(in a), ref Unsafe.AsRef(in b)));
            Assert.Equal(8L, a.Item8);
            Assert.Equal(big.Value, a);
        }

        [Fact]
        public void DocExamples()
        {
            using var arena = new SharedArena();
            Assert.Equal(10, arena.NewHandle().Alloc(10).Value);

            var list = arena.NewHandle().Alloc(new List<int> { 1, 2, 3 });
            Assert.Equal(new[] { 1, 2, 3 }, list.Value);

            SharedRef<string> scoped;
            {
                var handle = arena.NewHandle();
                scoped = handle.Alloc("scoped");
            }

            Assert.Equal("scoped", scoped.Value);
        }

        [Fact]
        public void FailingDisposalsAreCollected()
        {
            var drops = new List<int>();
            var arena = new SharedArena();
            var handle = arena.NewHandle();
            handle.Alloc(new SharedArenaDisposeRecorder(1, drops));
            handle.Alloc(new MemoryStream());
            handle.Alloc(new SharedArenaDisposeRecorder(2, null!));
            handle.Alloc(new SharedArenaDisposeRecorder(3, drops));
            var e = Assert.Throws<AggregateException>(arena.Dispose);
            Assert.Single(e.InnerExceptions);
            Assert.Equal(new[] { 3, 1 }, drops);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void AllocPreservesValues(int seed)
        {
            // egglog's shared_arena_alloc_preserves_values proptest: vec(any::<i64>(), 0..256).
            var random = new Random(seed);
            var values = Enumerable.Range(0, random.Next(0, 256)).Select(_ => random.NextInt64(long.MinValue, long.MaxValue)).ToArray();
            using var arena = new SharedArena();
            var handle = arena.NewHandle();
            var refs = values.Select(v => handle.Alloc(v)).ToArray();
            Assert.Equal(values, refs.Select(r => r.Value));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void DisposesEveryRegisteredValue(int seed)
        {
            // egglog's shared_arena_drops_every_registered_value proptest: vec(0usize..1024, 0..128).
            var random = new Random(seed);
            var values = Enumerable.Range(0, random.Next(0, 128)).Select(_ => random.Next(0, 1024)).ToArray();
            var drops = new List<int>();
            using (var arena = new SharedArena())
            {
                var handle = arena.NewHandle();
                foreach (var value in values)
                    handle.Alloc(new SharedArenaDisposeRecorder(value, drops));
            }

            Assert.Equal(values.Reverse(), drops);
        }

        [Fact]
        public void DisposesEveryHandlesValuesAfterParallelWorkers()
        {
            const int Threads = 6;
            const int PerThread = 50;
            var drops = new List<int>();
            var arena = new SharedArena();
            var threads = ConcurrencyTestThread.StartMany(Threads, t =>
            {
                var handle = arena.NewHandle();
                for (var i = 0; i < PerThread; i++)
                    handle.Alloc(new SharedArenaDisposeRecorder(t * PerThread + i, drops));
            });
            ConcurrencyTestThread.JoinAll(threads);
            arena.Dispose();

            Assert.Equal(Enumerable.Range(0, Threads * PerThread), drops.OrderBy(x => x));

            // Within each handle, last in first out.
            for (var t = 0; t < Threads; t++)
            {
                var mine = drops.Where(x => x / PerThread == t).ToArray();
                Assert.Equal(Enumerable.Range(t * PerThread, PerThread).Reverse(), mine);
            }
        }

    }

}
