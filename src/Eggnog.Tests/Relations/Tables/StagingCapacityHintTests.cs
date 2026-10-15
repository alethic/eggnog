using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the capacity a <see cref="MutationBuffer"/>'s staging buffers grow to once they outgrow their first
    /// small array: the row count of the buffer the table published last (<see cref="PendingState.StagingRowsHint"/>),
    /// capped at <see cref="MutationBuffer.MaxStartCells"/> cells. A buffer that stays within its first array never
    /// takes the larger one.
    /// </summary>
    public class StagingCapacityHintTests
    {

        /// <summary>
        /// The rows of arity 2 that a staging buffer's first array holds: <see cref="PooledArray.MinGrowLength"/> cells.
        /// </summary>
        const int FirstArrayRows = PooledArray.MinGrowLength / 2;

        [Fact]
        public void TheHintIsTheRowCountOfTheLastPublishedBuffer()
        {
            var state = new PendingState(new ShardData(4));
            Assert.Equal(0, state.StagingRowsHint);

            state.EnqueueRows(1, Rows(2, 300));
            Assert.Equal(300, state.StagingRowsHint);

            state.EnqueueRows(3, Rows(2, 7));
            Assert.Equal(7, state.StagingRowsHint);
            state.Clear();
        }

        [Fact]
        public void TheFirstBufferStartsEmptyAndGrows()
        {
            var state = new PendingState(new ShardData(1));
            Assert.Equal(FirstArrayRows, Stage(state, 1).Capacity);
            Assert.Equal(2 * FirstArrayRows, Stage(state, FirstArrayRows + 1).Capacity);
            state.Clear();
        }

        [Fact]
        public void ABufferThatOutgrowsItsFirstArrayGrowsStraightToTheLastPublishedRows()
        {
            var state = new PendingState(new ShardData(1));
            Stage(state, 300);
            var next = Stage(state, FirstArrayRows + 1);
            Assert.True(next.Capacity >= 300, $"The next buffer holds {next.Capacity} rows.");
            Assert.Equal(FirstArrayRows + 1, next.Count);
            state.Clear();
        }

        [Fact]
        public void AFewRowsAfterALargeFlushStayInTheFirstArray()
        {
            var state = new PendingState(new ShardData(1));
            Stage(state, 2000);
            Assert.Equal(FirstArrayRows, Stage(state, 1).Capacity);
            Assert.Equal(FirstArrayRows, Stage(state, FirstArrayRows).Capacity);
            state.Clear();
        }

        [Fact]
        public void TheHintFollowsAShrinkingFlush()
        {
            var state = new PendingState(new ShardData(1));
            state.EnqueueRows(0, Rows(2, 300));
            state.EnqueueRows(0, Rows(2, 5));
            Assert.Equal(2 * FirstArrayRows, Stage(state, FirstArrayRows + 1).Capacity);
            state.Clear();
        }

        [Fact]
        public void TheGrowthToTheHintIsCapped()
        {
            var state = new PendingState(new ShardData(1));
            state.EnqueueRows(0, Rows(2, 10_000));
            Assert.Equal(MutationBuffer.MaxStartCells / 2, Stage(state, FirstArrayRows + 1).Capacity);
            state.Clear();
        }

        [Fact]
        public void AStagingBufferGrowsToTheRequestedRowsOnItsFirstGrowthPastItsFirstArray()
        {
            var buffer = RowBuffer.ForStaging(5, 100);
            Assert.Same(Array.Empty<uint>(), buffer.Data);

            // The first array holds three rows of five cells; the next growth makes room for the hundred at once, and
            // the one after that doubles as usual.
            var row = new uint[5];
            for (var i = 0; i < 3; i++)
                buffer.AddRow(row);

            Assert.Equal(PooledArray.MinGrowLength / 5, buffer.Capacity);
            buffer.AddRow(row);
            var hinted = buffer.Capacity;
            Assert.True(hinted >= 100, $"The buffer holds {hinted} rows.");
            while (buffer.Count < hinted + 1)
                buffer.AddRow(row);

            Assert.True(buffer.Capacity >= 2 * hinted, $"The buffer holds {buffer.Capacity} rows.");
            buffer.Dispose();

            Assert.Throws<ArgumentOutOfRangeException>(() => RowBuffer.ForStaging(5, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => RowBuffer.ForStaging(0, 1));
        }

        /// <summary>
        /// Stages <paramref name="count"/> distinct rows through a fresh buffer of <paramref name="state"/>'s single
        /// shard, flushes it, and returns the row buffer it published.
        /// </summary>
        static RowBuffer Stage(PendingState state, int count)
        {
            var buffer = new MutationBuffer(state, 2, 1, true);
            for (uint i = 0; i < count; i++)
                buffer.StageInsert([i, i]);

            buffer.Flush();
            return state.RowQueue(0).Last();
        }

        /// <summary>
        /// A buffer of <paramref name="count"/> distinct rows of <paramref name="arity"/> cells.
        /// </summary>
        static RowBuffer Rows(int arity, int count)
        {
            var rows = new RowBuffer(arity);
            var row = new uint[arity];
            for (var i = 0; i < count; i++)
            {
                row[0] = (uint)i;
                rows.AddRow(row);
            }

            return rows;
        }

    }

}
