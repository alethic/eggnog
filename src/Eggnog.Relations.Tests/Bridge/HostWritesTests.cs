using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="HostWrites"/> (colored-contexts spec §10.4, X24): rows and unions kept bridge-side, replayed stamped with
    /// the flush's epoch, id cells canonicalized only for rows staged through the context API.
    /// </summary>
    public class HostWritesTests
    {

        [Fact]
        public void ReplayStampsCanonicalizesAndKeepsOrder()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            BridgeTestUtil.Union(g.EGraph, a, b);

            var writes = new HostWrites();
            Assert.True(writes.IsEmpty);
            writes.AddRow(g.Mark, [b.Rep, g.Unit.Rep], canonicalizeIds: true);
            writes.AddRow(g.Path, [b.Rep, b.Rep, g.Unit.Rep], canonicalizeIds: false);
            writes.AddUnion(a.Rep, b.Rep);
            Assert.Equal(2, writes.RowCount);
            Assert.Equal(1, writes.UnionCount);
            Assert.False(writes.IsEmpty);

            var timestamp = g.EGraph.NextTimestamp;
            var database = g.EGraph.Database;
            var state = database.CreateExecutionState(timestamp);
            writes.ReplayInto(state, timestamp, g.EGraph, g.Root.Context.UnionFind);
            state.Flush();
            database.MergeAll(timestamp);

            Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(g.Root, g.Mark));
            Assert.Equal([$"{b.Rep} {b.Rep}"], ContextGraph.Keys(g.Root, g.Path));
            var mark = BridgeTestUtil.PhysicalRow(g.EGraph, g.Mark, a.Rep);
            Assert.Equal(timestamp, mark[g.EGraph.GetFunctionInfo(g.Mark).Math.TimestampColumn]);

            // Replay does not clear; Clear keeps nothing; Release drops the storage.
            Assert.False(writes.IsEmpty);
            writes.Clear();
            Assert.True(writes.IsEmpty);
            writes.AddUnion(a.Rep, b.Rep);
            writes.Release();
            Assert.True(writes.IsEmpty);
        }

        /// <summary>
        /// Rows wider than the stack buffer and many rows grow the arenas; every row replays intact.
        /// </summary>
        [Fact]
        public void WideAndManyRowsReplayIntact()
        {
            var g = new ContextGraph();
            var keys = Enumerable.Repeat(ColumnTy.Id, 40).ToArray();
            var wide = g.Relation("wide", keys);
            var ids = Enumerable.Range(0, 40).Select(_ => g.Root.FreshId()).ToArray();
            for (var r = 0; r < 100; r++)
            {
                var row = ids.Select((v, i) => i == r % 40 ? g.Root.FreshId() : v).Append(g.Unit).ToArray();
                g.Root.StageRow(wide, row);
            }

            g.Root.FlushUpdates();
            Assert.Equal(100, g.Root.TableSize(wide));
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// <see cref="RuleProgress.SameConstants"/> compares executed constants by value.
        /// </summary>
        [Fact]
        public void ProgressComparesConstantsByValue()
        {
            Assert.True(RuleProgress.SameConstants(null, null));
            Assert.False(RuleProgress.SameConstants(null, []));
            Assert.True(RuleProgress.SameConstants([1, 2], [1, 2]));
            Assert.False(RuleProgress.SameConstants([1, 2], [2, 1]));
            Assert.False(RuleProgress.SameConstants([1], [1, 2]));
            Assert.Equal(0, default(RuleProgress).Generation);
            Assert.Equal("(none)", default(RuleProgress).ToString());
        }

    }

}
