using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the database part of <see cref="ExecutionState"/> (Actions/ExecutionState.Database.cs) and for
    /// <see cref="DatabaseView"/>.
    /// </summary>
    public class ExecutionStateDatabaseTests
    {

        [Fact]
        public void StatesOverABareCatalogHaveNoView()
        {
            var state = TableTestUtil.EmptyState();
            Assert.Null(state.View);
            Assert.Null(state.Clone().View);
            Assert.Throws<InvalidOperationException>(() => state.IncrementCounter(new CounterId(0)));
            Assert.Throws<InvalidOperationException>(() => state.ReadCounter(new CounterId(0)));
            Assert.Throws<InvalidOperationException>(() => state.GetTable(new TableId(0)));
            Assert.Throws<InvalidOperationException>(() => state.GetTableName(new TableId(0)));
            Assert.Throws<InvalidOperationException>(() => state.TryCallExternalFunction(new ExternalFunctionId(0), [], out _));
            Assert.Throws<ArgumentNullException>(() => new ExecutionState((DatabaseView)null!));
            Assert.False(DatabaseView.TryGetForCatalog(new TableCatalog(), out _));
        }

        [Fact]
        public void StatesOverTheViewStageThroughTheDatabase()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, KeepCurrentMerge.Instance), name: "f");
            var state = new ExecutionState(db.ReadOnlyView, 4);
            Assert.Same(db.ReadOnlyView, state.View);
            Assert.Same(db.Catalog, state.Catalog);
            Assert.Same(db.GetTable(f), state.GetTable(f));
            Assert.Equal("f", state.GetTableName(f));
            Assert.True(DatabaseView.TryGetForCatalog(db.Catalog, out var found));
            Assert.Same(db.ReadOnlyView, found);
            Assert.Same(db, db.ReadOnlyView.Owner);
            Assert.Same(db.Catalog.Notifications, db.ReadOnlyView.Notifications);

            // Staging notifies the database's list, so MergeAll finds the write.
            var clone = state.Clone();
            Assert.Same(db.ReadOnlyView, clone.View);
            clone.StageInsert(f, [1, 1, 4]);
            clone.Flush();
            Assert.True(db.MergeAll());
            Assert.Single(Rows(db, f));
        }

        [Fact]
        public void ExternalFunctionsCanBeCalledOnARangeOfAnArray()
        {
            // The overload compiled plans call: the arguments are a range of a larger buffer.
            var db = new Database(ParallelHeuristics.Serial);
            var sum = db.AddExternalFunction(ExternalFunction.Create((ExecutionState _, ReadOnlySpan<uint> args, out uint result) =>
            {
                result = 0;
                foreach (var a in args)
                    result += a;

                return args.IsEmpty == false;
            }));

            var state = db.CreateExecutionState();
            uint[] buffer = [100, 1, 2, 3, 200];
            Assert.True(state.TryCallExternalFunction(sum, buffer, 1, 3, out var result));
            Assert.Equal(6u, result);
            Assert.False(state.TryCallExternalFunction(sum, buffer, 2, 0, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.TryCallExternalFunction(sum, buffer, 3, 3, out _));
            Assert.Throws<KeyNotFoundException>(() => state.TryCallExternalFunction(new ExternalFunctionId(5), buffer, 0, 1, out _));
            Assert.Throws<InvalidOperationException>(() => TableTestUtil.EmptyState().TryCallExternalFunction(sum, buffer, 0, 1, out _));
        }

        [Fact]
        public void MergeFunctionsInAParallelInsertReachTheDatabase()
        {
            // A table's parallel insert runs its merge function in cloned states, which must still find the view.
            var parallelism = new ParallelHeuristics(4);
            var db = new Database(parallelism);
            var counter = db.AddCounter();
            var f = db.AddTable(new SortedWritesTable(1, 3, C(2), [], new CountingMaxMerge(counter, 1), parallelism));
            Stage(db, f, Enumerable.Range(0, 30_000).Select(i => R((uint)i, 0, 1)).ToArray());
            db.MergeAll();
            Stage(db, f, Enumerable.Range(0, 30_000).Select(i => R((uint)i, (uint)(i % 2), 2)).ToArray());

            Assert.True(db.MergeAll());
            Assert.Null(db.Failure);
            Assert.Equal(30_000, db.ReadCounter(counter));
            Assert.Equal(15_000, Rows(db, f).Count(r => r[1] == 1));
        }

        [Fact]
        public void TheViewHidesTablesTakenOut()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable());
            var f = db.AddTable(Function(1, KeepCurrentMerge.Instance));
            var g = db.AddTable(Function(1, KeepCurrentMerge.Instance));
            var probe = new ProbeOperation([uf, f, g], []);
            db.RunWithTakenOut(uf, [f], probe);

            // Inside the frame the rebuilder and the running table are hidden from every getter and from the listing.
            Assert.Equal(new[] { (f, uf, false), (f, f, false), (f, g, true) }, probe.Log.OrderBy(e => e.Watched.Rep).Select(e => (e.Table, e.Watched, e.Visible)));
            Assert.Empty(probe.Inconsistencies);
            Assert.Equal(new[] { g }, Assert.Single(probe.Listed).Visible);
            Assert.Equal(new[] { uf, f, g }, db.ReadOnlyView.TableIds);
        }

    }

}
