using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Database.ProcessConstraints(TableId, ReadOnlySpan{Constraint})"/> (egglog's
    /// <c>process_constraints</c>, core-relations/src/free_join/mod.rs:718-761) and <see cref="ProcessedConstraints"/>.
    /// </summary>
    public class DatabaseConstraintsTests
    {

        /// <summary>
        /// A table <c>[k, v, ts]</c> sorted by timestamp, with rows written at timestamps 1 and 2 and one deleted.
        /// </summary>
        static (Database Db, TableId Table) Setup()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, ClosureMerge.TakeNew));
            Stage(db, f, R(1, 10, 1), R(2, 20, 1), R(3, 10, 1));
            db.MergeAll();
            Stage(db, f, R(4, 10, 2), R(5, 50, 2));
            db.MergeAll();
            StageRemove(db, f, R(3));
            db.MergeAll();
            return (db, f);
        }

        [Fact]
        public void EqualitiesOnCacheableColumnsArePromotedThroughTheColumnIndex()
        {
            var (db, f) = Setup();
            using (var before = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 20)]))
            {
                Assert.Equal(new[] { Constraint.EqConst(C(1), 20) }, before.Fast);
                Assert.Empty(before.Slow);
                Assert.Equal(new[] { R(2, 20, 1) }, Rows(db.GetTable(f), before.Rows));
            }

            // Deleting after the index was built leaves the row listed (a deletion changes no version); scanning through
            // the table drops it.
            StageRemove(db, f, R(4));
            db.MergeAll();
            using var processed = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 10)]);
            Assert.Equal(2, processed.ApproximateSize);
            Assert.Equal(new[] { R(1, 10, 1) }, Rows(db.GetTable(f), processed.Rows));
            Assert.Equal(1, db.GetTableInfo(f).ColumnIndexes.Count);
        }

        [Fact]
        public void AnAbsentConstantEmptiesTheSubset()
        {
            var (db, f) = Setup();
            using var processed = db.ProcessConstraints(f, [Constraint.GeConst(C(2), 2), Constraint.EqConst(C(1), 99)]);

            Assert.Equal(new[] { Constraint.GeConst(C(2), 2), Constraint.EqConst(C(1), 99) }, processed.Fast);
            Assert.Equal(0, processed.ApproximateSize);
        }

        [Fact]
        public void TheTableAnswersItsOwnFastConstraintsFirst()
        {
            var (db, f) = Setup();
            var constraints = new[]
            {
                Constraint.Eq(C(0), C(1)),
                Constraint.EqConst(C(1), 10),
                Constraint.GtConst(C(0), 1),
                Constraint.GeConst(C(2), 2),
                Constraint.EqConst(C(0), 4),
            };
            using var processed = db.ProcessConstraints(f, constraints);

            // The range on the sorted timestamp is the table's; the two equalities are promoted after it, in order.
            Assert.Equal(new[] { Constraint.GeConst(C(2), 2), Constraint.EqConst(C(1), 10), Constraint.EqConst(C(0), 4) }, processed.Fast);
            Assert.Equal(new[] { Constraint.Eq(C(0), C(1)), Constraint.GtConst(C(0), 1) }, processed.Slow);
            Assert.Equal(new[] { R(4, 10, 2) }, Rows(db.GetTable(f), processed.Rows));
            Assert.Equal(2, db.GetTableInfo(f).ColumnIndexes.Count);
        }

        [Fact]
        public void WithoutFastConstraintsTheSubsetIsTheWholeTable()
        {
            var (db, f) = Setup();
            using var processed = db.ProcessConstraints(f, [Constraint.LtConst(C(1), 30)]);
            Assert.Empty(processed.Fast);
            Assert.Equal(new[] { Constraint.LtConst(C(1), 30) }, processed.Slow);
            Assert.Equal(5, processed.ApproximateSize);
            Assert.Equal(0, db.GetTableInfo(f).ColumnIndexes.Count);
        }

        [Fact]
        public void UncacheableColumnsStaySlow()
        {
            // The union-find's canonical column changes without a version bump, so it is never indexed; its key
            // column is answered by the table itself.
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable());
            Stage(db, uf, R(1, 2, 0), R(3, 4, 0));
            db.MergeAll();

            using var processed = db.ProcessConstraints(uf, [Constraint.EqConst(C(1), 1), Constraint.EqConst(C(0), 2)]);
            Assert.Equal(new[] { Constraint.EqConst(C(0), 2) }, processed.Fast);
            Assert.Equal(new[] { Constraint.EqConst(C(1), 1) }, processed.Slow);
            Assert.Equal(1, processed.ApproximateSize);
            Assert.Equal(0, db.GetTableInfo(uf).ColumnIndexes.Count);
        }

        [Fact]
        public void TheListOverloadAppends()
        {
            var (db, f) = Setup();
            var fast = new List<Constraint> { Constraint.LtConst(C(0), 1) };
            var slow = new List<Constraint> { Constraint.GtConst(C(0), 1) };
            var subset = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 20), Constraint.Eq(C(0), C(0))], fast, slow);

            Assert.Equal(new[] { Constraint.LtConst(C(0), 1), Constraint.EqConst(C(1), 20) }, fast);
            Assert.Equal(new[] { Constraint.GtConst(C(0), 1), Constraint.Eq(C(0), C(0)) }, slow);
            Assert.Equal(new[] { R(2, 20, 1) }, Rows(db.GetTable(f), subset.AsRef()));
            subset.Dispose();
            Assert.Throws<KeyNotFoundException>(() => db.ProcessConstraints(new TableId(7), []));
        }

        [Fact]
        public void ProcessedConstraintsOwnTheirSubset()
        {
            var (db, f) = Setup();
            var processed = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 10)]);
            var clone = processed.Clone();
            Assert.Same(processed.Fast, clone.Fast);
            Assert.Equal(processed.ApproximateSize, clone.ApproximateSize);

            var taken = processed.TakeSubset();
            Assert.Equal(0, processed.ApproximateSize);
            Assert.Equal(2, taken.Size);
            Assert.Equal(2, clone.ApproximateSize);
            taken.Dispose();
            processed.Dispose();
            clone.Dispose();
            clone.Dispose();

            using var dummy = ProcessedConstraints.Dummy();
            Assert.Equal(0, dummy.ApproximateSize);
            Assert.Empty(dummy.Fast);
            Assert.Empty(dummy.Slow);
        }

        [Fact]
        public void ProcessingIsSafeFromManyThreads()
        {
            var db = new Database(new ParallelHeuristics(4));
            var f = db.AddTable(Function(1, ClosureMerge.TakeNew, parallelism: new ParallelHeuristics(4)));
            Stage(db, f, Enumerable.Range(0, 30_000).Select(i => R((uint)i, (uint)(i % 97), 1)).ToArray());
            db.MergeAll();

            System.Threading.Tasks.Parallel.For(0, 97, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, v =>
            {
                using var processed = db.ProcessConstraints(f, [Constraint.EqConst(C(1), (uint)v)]);
                var expected = Enumerable.Range(0, 30_000).Count(i => i % 97 == v);
                Assert.Equal(expected, processed.ApproximateSize);
            });

            Assert.Equal(1, db.GetTableInfo(f).ColumnIndexes.Count);
        }

    }

}
