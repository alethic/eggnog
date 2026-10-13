using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="SubsetBuffer"/> and <see cref="BufferedSubset"/>: the arena of egglog's hash indexes
    /// (core-relations/src/hash_index/mod.rs:793-1013), its power-of-two blocks and size-class free lists, and the
    /// dense/sparse subsets stored in it.
    /// </summary>
    public class SubsetBufferTests
    {

        static int[] Rows(SubsetRef subset) => IndexTestUtil.ToArray(subset);

        [Fact]
        public void SizeClassesArePowersOfTwo()
        {
            Assert.Equal(0, SubsetBuffer.SizeClass(0));
            Assert.Equal(0, SubsetBuffer.SizeClass(1));
            Assert.Equal(1, SubsetBuffer.SizeClass(2));
            Assert.Equal(2, SubsetBuffer.SizeClass(3));
            Assert.Equal(2, SubsetBuffer.SizeClass(4));
            Assert.Equal(3, SubsetBuffer.SizeClass(5));
            Assert.Equal(10, SubsetBuffer.SizeClass(1024));
            Assert.Equal(11, SubsetBuffer.SizeClass(1025));
            Assert.Equal(31, SubsetBuffer.SizeClass(int.MaxValue));
            Assert.Equal(1L, SubsetBuffer.BlockSize(1));
            Assert.Equal(8L, SubsetBuffer.BlockSize(5));
        }

        [Fact]
        public void RunsOccupyPowerOfTwoBlocks()
        {
            var buffer = SubsetBuffer.Create();
            var a = buffer.NewRun([1, 2, 3]);
            Assert.True(a.IsSparse);
            Assert.Equal(0, a.Start);
            Assert.Equal(3, a.End);
            Assert.Equal(4, buffer.Length);

            var b = buffer.NewRun([7, 9, 11, 13, 15]);
            Assert.Equal(4, b.Start);
            Assert.Equal(12, buffer.Length);
            Assert.Equal([1, 2, 3], Rows(a.AsRef(buffer.Rows)));
            Assert.Equal([7, 9, 11, 13, 15], Rows(b.AsRef(buffer.Rows)));
        }

        [Fact]
        public void PushWritesInPlaceUntilTheBlockIsFull()
        {
            var buffer = SubsetBuffer.Create();
            var run = buffer.NewRun([1, 2, 3]);

            // Three rows in a block of four: the fourth goes in place.
            run = run.AddRowSorted(5, ref buffer);
            Assert.Equal(0, run.Start);
            Assert.Equal(4, buffer.Length);

            // The block is full: the run moves to a block of eight at the end, and the old block is freed.
            run = run.AddRowSorted(8, ref buffer);
            Assert.Equal(4, run.Start);
            Assert.Equal(12, buffer.Length);
            Assert.Equal([1, 2, 3, 5, 8], Rows(run.AsRef(buffer.Rows)));

            // A new run of up to four rows reuses the freed block.
            var other = buffer.NewRun([20, 21, 22, 23]);
            Assert.Equal(0, other.Start);
            Assert.Equal(12, buffer.Length);
            Assert.Equal([1, 2, 3, 5, 8], Rows(run.AsRef(buffer.Rows)));
            Assert.Equal([20, 21, 22, 23], Rows(other.AsRef(buffer.Rows)));
        }

        [Fact]
        public void FreeListsAreLastInFirstOutPerClass()
        {
            var buffer = SubsetBuffer.Create();
            var a = buffer.NewRun([1, 2]);
            var b = buffer.NewRun([3, 4]);
            var c = buffer.NewRun([5, 6, 7]);
            buffer.Free(a.Start, a.Size);
            buffer.Free(b.Start, b.Size);
            buffer.Free(c.Start, c.Size);

            // Class 1 (two slots) holds b then a; class 2 holds c.
            Assert.Equal(b.Start, buffer.Allocate(2));
            Assert.Equal(a.Start, buffer.Allocate(1 + 1));
            Assert.Equal(c.Start, buffer.Allocate(4));
            var end = buffer.Length;
            Assert.Equal(end, buffer.Allocate(2));
        }

        [Fact]
        public void DenseSubsetsGrowAtTheEndAndTurnSparseAtAGap()
        {
            var buffer = SubsetBuffer.Create();
            var subset = BufferedSubset.Empty;
            Assert.Equal(0, subset.Size);

            subset = subset.AddRowSorted(5, ref buffer);
            Assert.False(subset.IsSparse);
            Assert.Equal([5], Rows(subset.AsRef(buffer.Rows)));

            subset = subset.AddRowSorted(6, ref buffer).AddRowSorted(7, ref buffer);
            Assert.False(subset.IsSparse);
            Assert.Equal(5, subset.Start);
            Assert.Equal(8, subset.End);
            Assert.Equal(0, buffer.Length);

            subset = subset.AddRowSorted(10, ref buffer);
            Assert.True(subset.IsSparse);
            Assert.Equal(4, subset.Size);
            Assert.Equal([5, 6, 7, 10], Rows(subset.AsRef(buffer.Rows)));
            Assert.Equal(4, buffer.Length);
        }

        [Fact]
        public void RepeatingTheLastRowTurnsADenseSubsetSparse()
        {
            // egglog's add_row_sorted: a row equal to end - 1 is not end, so the range moves to the arena with a repeat.
            var buffer = SubsetBuffer.Create();
            var subset = BufferedSubset.Singleton(3).AddRowSorted(3, ref buffer);
            Assert.True(subset.IsSparse);
            Assert.Equal([3, 3], Rows(subset.AsRef(buffer.Rows)));
        }

        [Fact]
        public void RandomAppendsMatchAList()
        {
            var random = new Random(5);
            var buffer = SubsetBuffer.Create();
            var subsets = new BufferedSubset[40];
            var models = Enumerable.Range(0, subsets.Length).Select(_ => new List<int>()).ToArray();
            var next = new int[subsets.Length];
            for (var step = 0; step < 5000; step++)
            {
                var i = random.Next(subsets.Length);
                next[i] += random.Next(4) == 0 ? 0 : random.Next(1, 3);
                subsets[i] = subsets[i].AddRowSorted(next[i], ref buffer);
                models[i].Add(next[i]);
            }

            for (var i = 0; i < subsets.Length; i++)
                Assert.Equal(models[i], Rows(subsets[i].AsRef(buffer.Rows)));

            // Blocks are reused: the arena stays within a small factor of the rows stored.
            Assert.True(buffer.Length <= 4 * models.Sum(m => m.Count), $"arena of {buffer.Length} slots");
        }

        [Fact]
        public void CloneIsIndependentAndResetForgetsEverything()
        {
            var buffer = SubsetBuffer.Create();
            var run = buffer.NewRun([1, 4, 9]);
            var copy = buffer.Clone();
            run = run.AddRowSorted(16, ref buffer);
            Assert.Equal([1, 4, 9, 16], Rows(run.AsRef(buffer.Rows)));
            Assert.Equal([1, 4, 9], Rows(BufferedSubset.Sparse(0, 3).AsRef(copy.Rows)));

            buffer.Reset();
            Assert.Equal(0, buffer.Length);
            var fresh = buffer.NewRun([2, 3]);
            Assert.Equal(0, fresh.Start);

            // Release hands a poolable array back and keeps one too short for the pool.
            buffer.Release();
            Assert.Equal(0, buffer.Length);
            Assert.True(buffer.Rows.Length <= PooledArray.MaxUnpooledLength);
            copy.Release();
        }

        [Fact]
        public void ReserveAvoidsGrowth()
        {
            var buffer = SubsetBuffer.Create();
            buffer.Reserve(100);
            var rows = buffer.Rows;
            for (var i = 0; i < 25; i++)
                buffer.NewRun([i, i + 1, i + 2]);

            Assert.Same(rows, buffer.Rows);
        }

    }

}
