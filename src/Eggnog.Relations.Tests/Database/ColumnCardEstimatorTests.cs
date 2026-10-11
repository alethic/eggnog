using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ColumnCardEstimator"/> and <see cref="ColumnUniqueness"/> (egglog's <c>ColumnCardEst</c> and
    /// <c>ColUniqueness</c>, core-relations/src/free_join/mod.rs:878-976).
    /// </summary>
    public class ColumnCardEstimatorTests
    {

        [Fact]
        public void UniquenessPairsLiveRowsWithDistinctValues()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, ClosureMerge.TakeNew));
            Stage(db, f, R(1, 10, 1), R(2, 10, 1), R(3, 20, 1), R(4, 20, 1), R(5, 30, 1));
            db.MergeAll();
            var estimator = new ColumnCardEstimator(db);

            var key = estimator.GetColumnUniqueness(f, C(0));
            var value = estimator.GetColumnUniqueness(f, C(1));
            Assert.Equal(5ul, key.TableSize);
            Assert.Equal(5ul, key.ColumnSize);
            Assert.Equal(3ul, value.ColumnSize);
            Assert.True(key.CompareTo(value) < 0, "the key is more unique than the value");
            Assert.Equal(2, db.GetTableInfo(f).ColumnIndexes.Count);

            // After a merge the index refreshes; a deleted row's value stays counted until the next generation.
            Stage(db, f, R(6, 40, 2));
            StageRemove(db, f, R(5));
            db.MergeAll();
            value = estimator.GetColumnUniqueness(f, C(1));
            Assert.Equal(5ul, value.TableSize);
            Assert.Equal(4ul, value.ColumnSize);
        }

        [Fact]
        public void UniquenessOrdersByRowsPerValue()
        {
            Assert.True(new ColumnUniqueness(100, 50) == new ColumnUniqueness(10, 5));
            Assert.True(new ColumnUniqueness(10, 10).CompareTo(new ColumnUniqueness(10, 2)) < 0);
            Assert.Equal(new ColumnUniqueness(1, 1).TableSize, ColumnUniqueness.Identity.TableSize);
            var joined = new ColumnUniqueness(10, 3).Join(new ColumnUniqueness(4, 7));
            Assert.Equal(40ul, joined.TableSize);
            Assert.Equal(7ul, joined.ColumnSize);
            Assert.Equal(ulong.MaxValue, new ColumnUniqueness(ulong.MaxValue, 1).Join(new ColumnUniqueness(2, 1)).TableSize);
        }

        [Fact]
        public void ScaleSpreadsDistinctValuesEvenly()
        {
            var scaled = new ColumnUniqueness(100, 40).Scale(25);
            Assert.Equal(25ul, scaled.TableSize);
            Assert.Equal(10ul, scaled.ColumnSize);

            var empty = new ColumnUniqueness(0, 0).Scale(10);
            Assert.Equal(0ul, empty.TableSize);
            Assert.Equal(0ul, empty.ColumnSize);
            Assert.Equal(0ul, new ColumnUniqueness(10, 5).Scale(0).TableSize);

            // The product saturates rather than wrapping.
            Assert.Equal(ulong.MaxValue / 2, new ColumnUniqueness(2, ulong.MaxValue).Scale(4).ColumnSize);
        }

    }

}
