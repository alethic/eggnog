using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="DisplacedTable"/>: a port of egglog's core-relations/src/uf/tests.rs and unit tests of the
    /// union-find table's contract.
    /// </summary>
    public class DisplacedTableTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        static List<uint[]> Rows(DisplacedTable table, SubsetRef subset) => TableTestUtil.DumpSubset(table, subset).Select(x => x.Row).ToList();

        /// <summary>
        /// Port of <c>displaced</c>: unions become rows of displaced ids with a computed canonical column.
        /// </summary>
        [Fact]
        public void Displaced()
        {
            var state = TableTestUtil.EmptyState();
            var d = new DisplacedTable();
            var buffer = d.NewBuffer();
            buffer.StageInsert(R(0, 1, 0));
            buffer.StageInsert(R(2, 3, 0));
            buffer.Flush();
            d.Merge(state);

            var updates = new List<(uint, uint)>();
            foreach (var row in Rows(d, d.All().AsRef()))
            {
                Assert.Equal(0u, row[2]);
                updates.Add((row[0], row[1]));
            }

            Assert.Equal(2, updates.Count);
            Assert.NotEqual(updates[0], updates[1]);

            var eqFirst = d.Refine(d.All(), [Constraint.EqConst(C(0), updates[0].Item1)]);
            var rows = Rows(d, eqFirst.AsRef());
            Assert.Single(rows);
            Assert.Equal(R(updates[0].Item1, updates[0].Item2, 0), rows[0]);

            TableTestUtil.Stage(d, R(1, 3, 1));
            d.Merge(state);

            var updates2 = Rows(d, d.All().AsRef()).Select(row => (row[0], row[1])).ToList();
            Assert.Equal(3, updates2.Count);
            Assert.All(updates2, u => Assert.Equal(updates2[0].Item2, u.Item2));
        }

        [Fact]
        public void SpecMarksTheCanonicalColumnUncacheableAndForbidsDeletion()
        {
            var d = new DisplacedTable();
            Assert.Equal(TableKind.Displaced, d.Kind);
            Assert.Equal(1, d.Spec.KeyCount);
            Assert.Equal(2, d.Spec.ValueCount);
            Assert.Equal(3, d.Arity);
            Assert.False(d.Spec.AllowsDelete);
            Assert.True(d.Spec.IsUncacheable(C(1)));
            Assert.False(d.Spec.IsUncacheable(C(0)));
            Assert.False(d.HasStaleRows);
            Assert.Throws<InvalidOperationException>(() => d.NewBuffer().StageRemove(R(1)));
        }

        [Fact]
        public void MergeReportsEffectiveUnionsOnly()
        {
            var state = TableTestUtil.EmptyState();
            var d = new DisplacedTable();
            Assert.Equal(new TableChange(false, false), d.Merge(state));

            TableTestUtil.Stage(d, R(5, 7, 1), R(7, 5, 1));
            Assert.Equal(new TableChange(true, true), d.Merge(state));
            Assert.Equal(1, d.Count);

            TableTestUtil.Stage(d, R(7, 5, 2));
            Assert.Equal(new TableChange(false, false), d.Merge(state));
            Assert.Equal(new TableVersion(0, 1), d.Version);

            // Timestamps must not go backwards for an effective union.
            TableTestUtil.Stage(d, R(8, 9, 0));
            Assert.Throws<InvalidOperationException>(() => d.Merge(state));
        }

        [Fact]
        public void LookupsAnswerTheCanonicalColumnForAnyId()
        {
            var state = TableTestUtil.EmptyState();
            var d = new DisplacedTable();
            TableTestUtil.Stage(d, R(4, 2, 3), R(6, 4, 5));
            d.Merge(state);

            Assert.True(d.TryGetRowColumn(R(4), C(1), out var canonical));
            Assert.Equal(2u, canonical);
            Assert.True(d.TryGetRowColumn(R(6), C(1), out canonical));
            Assert.Equal(2u, canonical);
            Assert.True(d.TryGetRowColumn(R(100), C(1), out canonical));
            Assert.Equal(100u, canonical);
            Assert.True(d.TryGetRowColumn(R(2), C(1), out canonical));
            Assert.Equal(2u, canonical);

            Assert.False(d.TryGetRowColumn(R(2), C(2), out _));
            Assert.True(d.TryGetRowColumn(R(6), C(2), out var ts));
            Assert.Equal(5u, ts);
            Assert.True(d.TryGetColumn(6, 0, out var child));
            Assert.Equal(6u, child);

            var row = d.GetRow(R(6));
            Assert.NotNull(row);
            Assert.Equal(R(6, 2, 5), row.Value.Values);
            Assert.Equal(1, row.Value.Id);
            Assert.Null(d.GetRow(R(2)));
            Assert.Throws<ArgumentException>(() => d.GetRow(R(1, 2)));
            Assert.Throws<ArgumentOutOfRangeException>(() => d.TryGetColumn(6, 3, out _));
            Assert.Equal(2u, d.FindNaive(6));
        }

        [Fact]
        public void FastSubsetsUseTheLookupAndTheTimestampOrder()
        {
            var state = TableTestUtil.EmptyState();
            var d = new DisplacedTable();

            // Timestamps of rows 0..4: 1, 1, 3, 3, 5.
            TableTestUtil.Stage(d, R(10, 11, 1), R(12, 13, 1), R(14, 15, 3), R(16, 17, 3), R(18, 19, 5));
            d.Merge(state);
            Assert.Equal(5, d.Count);

            AssertFast(d, Constraint.EqConst(C(0), 13), 1, 2);
            AssertFast(d, Constraint.EqConst(C(0), 12), 0, 0);
            Assert.False(d.TryFastSubset(Constraint.EqConst(C(1), 10), out _));
            Assert.False(d.TryFastSubset(Constraint.Eq(C(0), C(1)), out _));
            Assert.False(d.TryFastSubset(Constraint.GeConst(C(1), 0), out _));
            Assert.False(d.TryFastSubset(Constraint.GeConst(C(0), 0), out _));

            AssertFast(d, Constraint.EqConst(C(2), 3), 2, 4);
            Assert.False(d.TryFastSubset(Constraint.EqConst(C(2), 2), out _));
            AssertFast(d, Constraint.LtConst(C(2), 3), 0, 2);
            AssertFast(d, Constraint.LtConst(C(2), 4), 0, 4);
            AssertFast(d, Constraint.LeConst(C(2), 3), 0, 4);
            AssertFast(d, Constraint.GtConst(C(2), 3), 4, 5);
            AssertFast(d, Constraint.GtConst(C(2), 2), 2, 5);
            AssertFast(d, Constraint.GeConst(C(2), 3), 2, 5);
            AssertFast(d, Constraint.GeConst(C(2), 6), 5, 5);
        }

        static void AssertFast(DisplacedTable table, Constraint constraint, int start, int end)
        {
            Assert.True(table.TryFastSubset(constraint, out var subset));
            Assert.Equal(end - start, subset.Size);
            if (end > start)
                Assert.Equal(Subset.Dense(start, end), subset);
        }

        [Fact]
        public void ScansExpandRowsAndApplyConstraintsToTheComputedColumn()
        {
            var state = TableTestUtil.EmptyState();
            var d = new DisplacedTable();
            TableTestUtil.Stage(d, R(1, 2, 1), R(3, 4, 1), R(2, 3, 2));
            d.Merge(state);

            // Rows: [2, 1, 1], [4, 3, 1], [3, 1, 2]; then everything is in class 1.
            var rows = Rows(d, d.All().AsRef());
            Assert.Equal(new[] { R(2, 1, 1), R(4, 1, 1), R(3, 1, 2) }, rows);

            var collector = new RowCollector();
            Assert.Equal(-1, d.ScanGenericBounded(d.All().AsRef(), 0, 10, [Constraint.GtConst(C(0), 2)], ref collector));
            Assert.Equal(new[] { 1, 2 }, collector.Rows.Select(x => x.Id));

            var refined = d.RefineOne(d.All(), Constraint.EqConst(C(1), 1));
            Assert.Equal(3, refined.Size);
            refined = d.RefineOne(refined, Constraint.EqConst(C(2), 1));
            Assert.Equal(Subset.Dense(0, 2), refined);
            Assert.Throws<ArgumentOutOfRangeException>(() => d.RefineOne(d.All(), Constraint.EqConst(C(3), 1)));

            var projected = new TaggedRowBuffer(2);
            Table table = d;
            Assert.Equal(1, table.ScanProject(d.All().AsRef(), [C(1), C(0)], 0, 1, ReadOnlySpan<Constraint>.Empty, projected));
            Assert.Equal(R(1, 2), projected.GetRow(0).ToArray());

            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var c = new RowCollector();
                d.ScanGeneric(SubsetRef.Dense(new OffsetRange(0, 4)), ref c);
            });
        }

        [Fact]
        public void ClearEmptiesTheLookupAndStartsANewGeneration()
        {
            var state = TableTestUtil.EmptyState();
            var d = new DisplacedTable();
            d.Clear();
            Assert.Equal(new TableVersion(0, 0), d.Version);

            TableTestUtil.Stage(d, R(1, 2, 1));
            d.Merge(state);
            TableTestUtil.Stage(d, R(3, 4, 1));
            d.Clear();

            Assert.Equal(new TableVersion(1, 0), d.Version);
            Assert.Equal(0, d.Count);
            Assert.Null(d.GetRow(R(2)));
            Assert.Equal(2u, d.FindNaive(2));
            Assert.Equal(new TableChange(false, false), d.Merge(state));

            TableTestUtil.Stage(d, R(2, 5, 0));
            d.Merge(state);
            Assert.Equal(R(5, 2, 0), d.GetRow(R(5))!.Value.Values);
        }

        [Fact]
        public void ClonesAreIndependentAndDoNotCopyStagedUnions()
        {
            var state = TableTestUtil.EmptyState();
            var d = new DisplacedTable();
            TableTestUtil.Stage(d, R(1, 2, 1));
            d.Merge(state);
            TableTestUtil.Stage(d, R(3, 4, 2));

            var clone = (DisplacedTable)d.Clone();
            Assert.Equal(d.Version, clone.Version);
            Assert.Equal(new TableChange(false, false), clone.Merge(state));
            Assert.Equal(new TableChange(true, true), d.Merge(state));
            Assert.Equal(2, d.Count);
            Assert.Equal(1, clone.Count);

            TableTestUtil.Stage(clone, R(1, 0, 3));
            clone.Merge(state);
            Assert.Equal(0u, clone.FindNaive(2));
            Assert.Equal(1u, d.FindNaive(2));
        }

        [Fact]
        public void UpdatesSinceListsTheUnionsAppendedSinceAVersion()
        {
            var state = TableTestUtil.EmptyState();
            var d = new DisplacedTable();
            TableTestUtil.Stage(d, R(1, 2, 1));
            d.Merge(state);
            var version = d.Version;
            TableTestUtil.Stage(d, R(3, 4, 2), R(5, 6, 2));
            d.Merge(state);

            Assert.Equal(Subset.Dense(1, 3), d.UpdatesSince(version.Minor));
            Assert.Equal(new uint[] { 4, 6 }, Rows(d, d.UpdatesSince(version.Minor).AsRef()).Select(r => r[0]));
            Assert.Throws<ArgumentOutOfRangeException>(() => d.UpdatesSince(4));
        }

    }

}
