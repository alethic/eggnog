using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the table layer's part of <see cref="ExecutionState"/>, for <see cref="TableCatalog"/> and for
    /// <see cref="ExecutionSignals"/>.
    /// </summary>
    public class ExecutionStateTests
    {

        static uint[] R(params uint[] cells) => cells;

        static (TableCatalog Catalog, TableId Id, SortedWritesTable Table) Setup()
        {
            var catalog = new TableCatalog();
            var table = new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            var id = catalog.Add(table);
            return (catalog, id, table);
        }

        [Fact]
        public void StagingCreatesOneBufferPerTableAndNotifies()
        {
            var (catalog, id, table) = Setup();
            var state = new ExecutionState(catalog, 7);
            Assert.False(state.Changed);
            Assert.Equal(7u, state.Timestamp);
            Assert.Same(catalog, state.Catalog);

            state.StageInsert(id, R(1, 10));
            state.StageInsert(id, new uint[] { 0, 2, 20 }.AsSpan(1));
            Assert.True(state.Changed);
            Assert.Same(state.GetBuffer(id), state.GetBuffer(id));
            Assert.Equal(new[] { id }, catalog.Notifications.Reset());

            // Nothing reaches the table before the state flushes.
            table.Merge(state);
            Assert.Equal(0, table.Count);
            state.Flush();
            table.Merge(state);
            Assert.Equal(2, table.Count);

            state.Changed = false;
            state.StageRemove(id, R(1));
            state.StageRemove(id, new uint[] { 9, 2 }.AsSpan(1));
            Assert.True(state.Changed);
            state.Flush();
            Assert.True(table.Merge(state).Removed);
            Assert.Equal(0, table.Count);
            Assert.Throws<KeyNotFoundException>(() => state.StageInsert(new TableId(9), R(1, 1)));
        }

        [Fact]
        public void ClonesStageIntoFreshBuffersAndShareSignals()
        {
            var (catalog, id, table) = Setup();
            var state = new ExecutionState(catalog, 3);
            state.StageInsert(id, R(1, 1));

            var clone = state.Clone();
            Assert.False(clone.Changed);
            Assert.Equal(3u, clone.Timestamp);
            Assert.Same(state.Signals, clone.Signals);
            Assert.NotSame(state.GetBuffer(id), clone.GetBuffer(id));

            clone.StageInsert(id, R(2, 2));
            clone.Flush();
            table.Merge(state);
            Assert.Equal(new[] { 2u }, TableTestUtil.LiveRows(table).ConvertAll(r => r[0]));

            state.Flush();
            table.Merge(state);
            Assert.Equal(2, table.Count);

            Assert.False(state.ShouldStop);
            clone.TriggerEarlyStop();
            Assert.True(state.ShouldStop);
        }

        [Fact]
        public void ParallelClonesFlushIndependently()
        {
            var (catalog, id, table) = Setup();
            var state = new ExecutionState(catalog);
            Parallel.For(0, 8, i =>
            {
                var local = state.Clone();
                for (uint k = 0; k < 1000; k++)
                    local.StageInsert(id, R((uint)i * 1000 + k, k));

                local.Flush();
            });

            table.Merge(state);
            Assert.Equal(8000, table.Count);
        }

        [Fact]
        public void TheFirstFailureWins()
        {
            var state = TableTestUtil.EmptyState();
            Assert.Null(state.Failure);
            state.ReportFailure("first");
            state.Clone().ReportFailure("second");
            Assert.Equal("first", state.Failure);
            Assert.True(state.ShouldStop);
        }

        [Fact]
        public void CatalogMapsIdsToTables()
        {
            var catalog = new TableCatalog();
            var a = new DisplacedTable();
            var b = new DisplacedTable();
            Assert.Equal(new TableId(0), catalog.Add(a));
            Assert.Equal(new TableId(1), catalog.Add(b));
            Assert.Equal(2, catalog.Count);
            Assert.Same(b, catalog[new TableId(1)]);
            catalog.Set(new TableId(0), b);
            Assert.True(catalog.TryGetTable(new TableId(0), out var found));
            Assert.Same(b, found);
            Assert.False(catalog.TryGetTable(new TableId(5), out _));
            Assert.Throws<ArgumentNullException>(() => catalog.Add(null!));
        }

    }

}
