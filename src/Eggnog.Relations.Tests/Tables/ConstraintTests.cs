using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Constraint"/>, <see cref="TableSpec"/>, <see cref="TableVersion"/> and
    /// <see cref="ParallelHeuristics"/>: the small value types of the table layer.
    /// </summary>
    public class ConstraintTests
    {

        static ColumnId C(int column) => new(column);

        [Fact]
        public void EvaluatesEveryKindWithUnsignedComparisons()
        {
            uint[] row = [5, 7, 7, uint.MaxValue - 1];
            Assert.True(Constraint.Eq(C(1), C(2)).Evaluate(row));
            Assert.False(Constraint.Eq(C(0), C(1)).Evaluate(row));
            Assert.True(Constraint.EqConst(C(0), 5).Evaluate(row));
            Assert.False(Constraint.EqConst(C(0), 6).Evaluate(row));
            Assert.True(Constraint.LtConst(C(0), 6).Evaluate(row));
            Assert.False(Constraint.LtConst(C(0), 5).Evaluate(row));
            Assert.True(Constraint.LeConst(C(0), 5).Evaluate(row));
            Assert.True(Constraint.GtConst(C(3), 7).Evaluate(row));
            Assert.False(Constraint.GtConst(C(0), 5).Evaluate(row));
            Assert.True(Constraint.GeConst(C(0), 5).Evaluate(row));
            Assert.False(Constraint.GeConst(C(0), 6).Evaluate(row));

            uint[] padded = [99, 5, 7, 7, 1];
            Assert.True(Constraint.Eq(C(1), C(2)).Evaluate(padded, 1));
            Assert.True(Constraint.EqConst(C(0), 5).Evaluate(padded, 1));
            Assert.Throws<IndexOutOfRangeException>(() => Constraint.EqConst(C(4), 5).Evaluate(padded, 1));

            Assert.True(Constraint.EvaluateAll([Constraint.EqConst(C(0), 5), Constraint.Eq(C(1), C(2))], row));
            Assert.False(Constraint.EvaluateAll([Constraint.EqConst(C(0), 5), Constraint.Eq(C(0), C(2))], row));
            Assert.True(Constraint.EvaluateAll(ReadOnlySpan<Constraint>.Empty, row));
        }

        [Fact]
        public void ExposesItsPartsAndFormats()
        {
            var eq = Constraint.Eq(C(1), C(3));
            Assert.Equal((ConstraintKind.Eq, 1, 3, 0u), (eq.Kind, eq.Column, eq.OtherColumn, eq.Constant));
            var ge = Constraint.GeConst(C(2), 9);
            Assert.Equal((ConstraintKind.GeConst, 2, -1, 9u), (ge.Kind, ge.Column, ge.OtherColumn, ge.Constant));
            Assert.Equal("c1 == c3", eq.ToString());
            Assert.Equal("c2 >= 9", ge.ToString());
            Assert.Equal("c0 < 4", Constraint.LtConst(C(0), 4).ToString());
            Assert.Equal(Constraint.EqConst(C(1), 2), Constraint.EqConst(C(1), 2));
            Assert.NotEqual(Constraint.EqConst(C(1), 2), Constraint.LeConst(C(1), 2));
            Assert.True(ge.Compare(9));
            Assert.Throws<InvalidOperationException>(() => eq.Compare(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Constraint.EqConst(C(-1), 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Constraint.Eq(C(0), C(-2)));
        }

        [Fact]
        public void TableSpecDescribesTheSchema()
        {
            var spec = new TableSpec(2, 3, true, [C(4)]);
            Assert.Equal(2, spec.KeyCount);
            Assert.Equal(3, spec.ValueCount);
            Assert.Equal(5, spec.Arity);
            Assert.True(spec.AllowsDelete);
            Assert.True(spec.IsUncacheable(C(4)));
            Assert.False(spec.IsUncacheable(C(3)));
            Assert.False(spec.IsUncacheable(C(9)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TableSpec(1, 1, false, [C(2)]));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TableSpec(-1, 1, false));
        }

        [Fact]
        public void ParallelHeuristicsUseEgglogsThresholds()
        {
            var serial = ParallelHeuristics.Serial;
            Assert.Equal(1, serial.DegreeOfParallelism);
            Assert.Equal(1, serial.ShardData.Count);
            Assert.False(serial.ParallelizeTableOp(1_000_000));
            Assert.False(serial.ParallelizeRebuild(1_000_000));

            var four = new ParallelHeuristics(4);
            Assert.Equal(8, four.ShardData.Count);
            Assert.Equal(4, four.Options.MaxDegreeOfParallelism);
            Assert.False(four.ParallelizeTableOp(20_000));
            Assert.True(four.ParallelizeTableOp(20_001));
            Assert.False(four.ParallelizeIndexConstruction(20_000));
            Assert.True(four.ParallelizeIndexConstruction(20_001));
            Assert.False(four.ParallelizeRebuild(10_000));
            Assert.True(four.ParallelizeRebuild(10_001));
            Assert.False(four.ParallelizeDbLevelOp(10_000));
            Assert.True(four.ParallelizeDbLevelOp(10_001));
            Assert.False(four.ParallelizeIntraContainerOp(1_000));
            Assert.True(four.ParallelizeIntraContainerOp(1_001));
            Assert.False(four.ParallelizeInterContainerOp(1));
            Assert.True(four.ParallelizeInterContainerOp(2));
            Assert.True(ParallelHeuristics.Default.DegreeOfParallelism >= 1);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelHeuristics(0));
        }

    }

}
