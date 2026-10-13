using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="UnionContainerMerge"/> (egglog-bridge/src/lib.rs:242-257),
    /// <see cref="ContainerRebuildSummary"/> (core-relations/src/containers/mod.rs:76-122) and
    /// <see cref="OrderedValueSet"/>, which holds a summary's dirty ids.
    /// </summary>
    public class ContainerMergeTests
    {

        [Fact]
        public void UnionContainerMergeUnionsDistinctIdsAndKeepsTheSmaller()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable());
            var clock = db.AddCounter();
            for (var i = 0; i < 4; i++)
                db.IncrementCounter(clock);

            var withClock = new UnionContainerMerge(uf, clock);
            var withState = new UnionContainerMerge(uf);
            Assert.Equal(uf, withClock.UnionFind);
            Assert.Equal(clock, withClock.TimestampCounter);
            Assert.Null(withState.TimestampCounter);

            db.WithExecutionState(es =>
            {
                Assert.Equal(5u, withClock.Merge(es, 5, 5));
                Assert.False(es.Changed);
                Assert.Equal(3u, withClock.Merge(es, 9, 3));
                Assert.Equal(6u, withState.Merge(es, 6, 8));
                Assert.True(es.Changed);
                return 0;
            }, timestamp: 4);
            db.MergeAll();

            Assert.Equal(new[] { R(8, 6, 4), R(9, 3, 4) }, Rows(db, uf));
        }

        [Fact]
        public void SummariesNoteChangesAndOrderedDirtyIds()
        {
            var summary = new ContainerRebuildSummary();
            Assert.False(summary.Changed);
            summary.NoteChange();
            Assert.True(summary.Changed);
            Assert.Equal(0, summary.DirtyCount);

            var other = new ContainerRebuildSummary();
            Assert.True(other.NoteDirtyId(9));
            Assert.True(other.NoteDirtyId(4));
            Assert.False(other.NoteDirtyId(9));
            Assert.True(other.Changed);
            Assert.True(other.IsDirty(4));
            Assert.False(other.IsDirty(5));

            var combined = new ContainerRebuildSummary();
            combined.NoteDirtyId(4);
            combined.Extend(other);
            Assert.Equal(new uint[] { 4, 9 }, combined.DirtyIds.ToArray());
            Assert.True(combined.Changed);

            var quiet = new ContainerRebuildSummary();
            quiet.Extend(new ContainerRebuildSummary());
            Assert.False(quiet.Changed);
        }

        [Fact]
        public void OrderedValueSetsKeepInsertionOrderWithoutRepeats()
        {
            var set = new OrderedValueSet();
            Assert.True(set.IsEmpty);
            set.AddRange([5, 1, 5, 3]);
            Assert.False(set.Add(1));
            Assert.Equal(new uint[] { 5, 1, 3 }, set.ToArray());
            Assert.Equal(3, set.Count);
            Assert.Equal(1u, set[1]);
            Assert.True(set.Contains(3));
            set.Clear();
            Assert.True(set.IsEmpty);
            Assert.False(set.Contains(3));
        }

    }

}
