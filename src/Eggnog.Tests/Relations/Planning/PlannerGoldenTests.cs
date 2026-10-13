using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Golden stage lists for the planner experiments: the plans egglog prints for the same queries
    /// (<see cref="ExperimentQueries"/>) and the same table contents, in this port's text format.
    /// </summary>
    /// <remarks>
    /// Differences from egglog's output, each deliberate:
    /// <list type="bullet">
    /// <item>Probe and filter lists of Free Join stages are sorted by atom id; egglog drains an FxHash map. Only
    /// <c>const_repeat</c> under PureSize differs (egglog prints the scans <c>[a0, a2, a1]</c>).</item>
    /// <item>Atoms without variables get an <c>exists</c> check that a visible row exists, where egglog only checks
    /// that the header is non-empty: <c>allconst</c>.</item>
    /// <item>MinCover with a variable-free atom, on which egglog panics, plans like PureSize: <c>allconst</c>.</item>
    /// <item>Free Join with decomposition, on which egglog panics, plans path4 and rect under PureSize and MinCover;
    /// <see cref="PlannerDecompositionTests"/> covers them.</item>
    /// </list>
    /// Two other departures change none of these plans: the chain walk absorbs every child of a chain node (egglog's
    /// <c>all_children[1..]</c> orphans the first), and the result block joins a block that no later block constrains
    /// through a <c>Lookup</c> stage (egglog's result block skips such a block and loses its join constraint). Every
    /// chain node here has at most one child, and every block with message variables contributes a prologue.
    /// </remarks>
    public class PlannerGoldenTests
    {

        static string[] Plan(string name, PlanStrategy strategy, bool noDecomposition = false)
        {
            var (db, query) = ExperimentQueries.Build(name, strategy, noDecomposition);
            return PlanLines.Of(QueryPlanner.PlanQuery(query, db));
        }

        [Fact]
        public void TriangleGj()
        {
            Assert.Equal(
            [
                "SinglePlan",
                "[0] Intersect v0 [a0.c0, a2.c1]",
                "[1] Intersect v1 [a0.c1, a1.c0]",
                "[2] Intersect v2 [a1.c1, a2.c0]",
            ], Plan("triangle", PlanStrategy.Gj));
        }

        [Theory]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void TriangleFreeJoin(string strategy)
        {
            Assert.Equal(
            [
                "SinglePlan",
                "[0] FusedIntersect a1[c0,c1] bind[c0->v1, c1->v2] probe[a0[c1] key[0], a2[c0] key[1]]",
                "[1] Intersect v0 [a2.c1, a0.c0]",
            ], Plan("triangle", Enum.Parse<PlanStrategy>(strategy)));
        }

        [Fact]
        public void Path3Gj()
        {
            Assert.Equal(
            [
                "SinglePlan",
                "[0] FusedIntersect a0[c0] bind[c0->v0]",
                "[1] Intersect v1 [a0.c1, a1.c0]",
                "[2] Intersect v2 [a1.c1, a2.c0]",
                "[3] FusedIntersect a2[c1] bind[c1->v3]",
            ], Plan("path3", PlanStrategy.Gj));
        }

        [Fact]
        public void Path4Gj()
        {
            Assert.Equal(
            [
                "DecomposedPlan",
                "block 0 msg[v2] val[v4]",
                "[0] Intersect v2 [a1.c1, a2.c0]",
                "[1] Intersect v3 [a2.c1, a3.c0]",
                "[2] FusedIntersect a3[c1] bind[c1->v4]",
                "block 1 msg[] val[v0,v2]",
                "[0] Mat m0 KeyOnly bind[c0->v2] probe[a1[c1] key[0], a2[c0] key[0]]",
                "[1] FusedIntersect a0[c0] bind[c0->v0]",
                "[2] Intersect v1 [a0.c1, a1.c0]",
                "result",
                "[0] Mat m1 Full bind[c0->v0, c1->v2]",
                "[1] Mat m0 Value[v2] bind[c0->v4]",
            ], Plan("path4", PlanStrategy.Gj));
        }

        [Fact]
        public void Path4NoDecompositionGj()
        {
            Assert.Equal(
            [
                "SinglePlan",
                "[0] FusedIntersect a0[c0] bind[c0->v0]",
                "[1] Intersect v1 [a0.c1, a1.c0]",
                "[2] Intersect v2 [a1.c1, a2.c0]",
                "[3] Intersect v3 [a2.c1, a3.c0]",
                "[4] FusedIntersect a3[c1] bind[c1->v4]",
            ], Plan("path4", PlanStrategy.Gj, noDecomposition: true));
        }

        [Fact]
        public void Path4NoDecompositionPureSize()
        {
            Assert.Equal(
            [
                "SinglePlan",
                "[0] FusedIntersect a1[c0,c1] bind[c0->v1, c1->v2] probe[a0[c1] key[0], a2[c0] key[1]]",
                "[1] FusedIntersect a3[c0,c1] bind[c0->v3, c1->v4] probe[a2[c1] key[0]]",
                "[2] FusedIntersect a0[c0] bind[c0->v0]",
            ], Plan("path4", PlanStrategy.PureSize, noDecomposition: true));
        }

        [Fact]
        public void Path6Gj()
        {
            Assert.Equal(
            [
                "DecomposedPlan",
                "block 0 msg[v3] val[v0]",
                "[0] FusedIntersect a0[c0] bind[c0->v0]",
                "[1] Intersect v1 [a0.c1, a1.c0]",
                "[2] Intersect v2 [a1.c1, a2.c0]",
                "[3] Intersect v3 [a2.c1, a3.c0]",
                "block 1 msg[] val[v3,v6]",
                "[0] Mat m0 KeyOnly bind[c0->v3] probe[a2[c1] key[0], a3[c0] key[0]]",
                "[1] Intersect v4 [a3.c1, a4.c0]",
                "[2] Intersect v5 [a4.c1, a5.c0]",
                "[3] FusedIntersect a5[c1] bind[c1->v6]",
                "result",
                "[0] Mat m1 Full bind[c0->v3, c1->v6]",
                "[1] Mat m0 Value[v3] bind[c0->v0]",
            ], Plan("path6", PlanStrategy.Gj));
        }

        [Fact]
        public void RectangleGj()
        {
            Assert.Equal(
            [
                "DecomposedPlan",
                "block 0 msg[v0,v2] val[]",
                "[0] Intersect v0 [a0.c0, a3.c1]",
                "[1] Intersect v1 [a0.c1, a1.c0]",
                "[2] Intersect v2 [a1.c1, a2.c0]",
                "block 1 msg[] val[v0,v2]",
                "[0] Mat m0 KeyOnly bind[c0->v0, c1->v2] probe[a0[c0] key[0], a3[c1] key[0], a1[c1] key[1], a2[c0] key[1]]",
                "[1] Intersect v3 [a2.c1, a3.c0]",
                "result",
                "[0] Mat m1 Full bind[c0->v0, c1->v2]",
            ], Plan("rect", PlanStrategy.Gj));
        }

        [Fact]
        public void StarGj()
        {
            Assert.Equal(
            [
                "SinglePlan",
                "[0] Intersect v0 [a0.c0, a1.c0, a2.c0]",
                "[1] FusedIntersect a0[c1] bind[c1->v1]",
                "[2] FusedIntersect a1[c1] bind[c1->v2]",
                "[3] FusedIntersect a2[c1] bind[c1->v3]",
            ], Plan("star", PlanStrategy.Gj));
        }

        [Theory]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void StarFreeJoin(string strategy)
        {
            Assert.Equal(
            [
                "SinglePlan",
                "[0] FusedIntersect a1[c0,c1] bind[c0->v0, c1->v2] probe[a0[c0] key[0], a2[c0] key[0]]",
                "[1] FusedIntersect a2[c1] bind[c1->v3]",
                "[2] FusedIntersect a0[c1] bind[c1->v1]",
            ], Plan("star", Enum.Parse<PlanStrategy>(strategy)));
        }

        [Fact]
        public void Star4Gj()
        {
            Assert.Equal(
            [
                "SinglePlan",
                "[0] Intersect v0 [a0.c0, a1.c0, a2.c0, a3.c0]",
                "[1] FusedIntersect a0[c1] bind[c1->v1]",
                "[2] FusedIntersect a1[c1] bind[c1->v2]",
                "[3] FusedIntersect a2[c1] bind[c1->v3]",
                "[4] FusedIntersect a3[c1] bind[c1->v4]",
            ], Plan("star4", PlanStrategy.Gj));
        }

        [Fact]
        public void SpiderGj()
        {
            Assert.Equal(
            [
                "DecomposedPlan",
                "block 0 msg[v0] val[v3]",
                "[0] Intersect v0 [a0.c0, a3.c0, a6.c0]",
                "[1] Intersect v1 [a0.c1, a1.c0]",
                "[2] Intersect v2 [a1.c1, a2.c0]",
                "[3] FusedIntersect a2[c1] bind[c1->v3]",
                "block 1 msg[v0] val[v9]",
                "[0] Mat m0 KeyOnly bind[c0->v0] probe[a0[c0] key[0], a3[c0] key[0], a6[c0] key[0]]",
                "[1] Intersect v7 [a6.c1, a7.c0]",
                "[2] Intersect v8 [a7.c1, a8.c0]",
                "[3] FusedIntersect a8[c1] bind[c1->v9]",
                "block 2 msg[] val[v0,v6]",
                "[0] Mat m1 KeyOnly bind[c0->v0] probe[a0[c0] key[0], a3[c0] key[0], a6[c0] key[0]]",
                "[1] Intersect v4 [a3.c1, a4.c0]",
                "[2] Intersect v5 [a4.c1, a5.c0]",
                "[3] FusedIntersect a5[c1] bind[c1->v6]",
                "result",
                "[0] Mat m2 Full bind[c0->v0, c1->v6]",
                "[1] Mat m1 Value[v0] bind[c0->v9]",
                "[2] Mat m0 Value[v0] bind[c0->v3]",
            ], Plan("spider", PlanStrategy.Gj));
        }

        [Fact]
        public void Tree5Gj()
        {
            Assert.Equal(
            [
                "DecomposedPlan",
                "block 0 msg[v1] val[v0,v3]",
                "[0] FusedIntersect a0[c0] bind[c0->v0]",
                "[1] Intersect v1 [a0.c1, a1.c0, a3.c0]",
                "[2] Intersect v2 [a1.c1, a2.c0]",
                "[3] FusedIntersect a2[c1] bind[c1->v3]",
                "block 1 msg[] val[v1,v5]",
                "[0] Mat m0 KeyOnly bind[c0->v1] probe[a0[c1] key[0], a1[c0] key[0], a3[c0] key[0]]",
                "[1] Intersect v4 [a3.c1, a4.c0]",
                "[2] FusedIntersect a4[c1] bind[c1->v5]",
                "result",
                "[0] Mat m1 Full bind[c0->v1, c1->v5]",
                "[1] Mat m0 Value[v1] bind[c0->v0, c1->v3]",
            ], Plan("tree5", PlanStrategy.Gj));
        }

        [Fact]
        public void ConstRepeatGj()
        {
            Assert.Equal(
            [
                "SinglePlan",
                "header a0{c1 == 3}",
                "[0] Intersect v0 [a0.c0, a1.c0{c1 == c0}, a2.c0{c1 > 2}]",
                "[1] FusedIntersect a2[c1] bind[c1->v1]",
            ], Plan("const_repeat", PlanStrategy.Gj));
        }

        [Fact]
        public void ConstRepeatPureSize()
        {
            // egglog prints the scans in hash order, [a0.c0, a2.c0{c1 > 2}, a1.c0{c1 == c0}].
            Assert.Equal(
            [
                "SinglePlan",
                "header a0{c1 == 3}",
                "[0] Intersect v0 [a0.c0, a1.c0{c1 == c0}, a2.c0{c1 > 2}]",
                "[1] FusedIntersect a2[c1] bind[c1->v1]",
            ], Plan("const_repeat", PlanStrategy.PureSize));
        }

        [Fact]
        public void ConstRepeatMinCover()
        {
            Assert.Equal(
            [
                "SinglePlan",
                "header a0{c1 == 3}",
                "[0] FusedIntersect a2[c0,c1]{c1 > 2} bind[c0->v0, c1->v1] probe[a0[c0] key[0], a1[c0,c1]{c1 == c0} key[0,0]]",
            ], Plan("const_repeat", PlanStrategy.MinCover));
        }

        [Fact]
        public void Join2UnusedGj()
        {
            Assert.Equal(
            [
                "SinglePlan",
                "[0] FusedIntersect a0[c0] bind[c0->v0]",
                "[1] Intersect v1 [a0.c1, a1.c0]",
            ], Plan("join2_unused", PlanStrategy.Gj));
        }

        [Theory]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void Join2UnusedFreeJoin(string strategy)
        {
            Assert.Equal(
            [
                "SinglePlan",
                "[0] FusedIntersect a1[c0,c1] bind[c0->v1, c1->v2] probe[a0[c1] key[0]]",
                "[1] FusedIntersect a0[c0] bind[c0->v0]",
            ], Plan("join2_unused", Enum.Parse<PlanStrategy>(strategy)));
        }

        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void DegenerateQueries(string strategy)
        {
            var db = new PlanTestDatabase();
            var t = db.AddTable(2, 2, PlanTestDatabase.ExperimentRows(10, 5));

            // No atoms: an empty single plan.
            var empty = new QueryBuilder(db).Build(new ActionId(0));
            Assert.Equal(["SinglePlan"], PlanLines.Of(QueryPlanner.PlanQuery(empty, db, Enum.Parse<PlanStrategy>(strategy), false)));

            // Only variable-free atoms, more than two: nothing to decompose, three checks, no stage. A variable the
            // action defines has no occurrence and is ignored.
            var qb = new QueryBuilder(db);
            var defined = qb.NewVariable();
            qb.AddAtom(t, [new Value(0), new Value(3)]);
            qb.AddAtom(t, [QueryEntry.Wildcard(), new Value(10)]);
            qb.AddAtom(t, [QueryEntry.Wildcard(), QueryEntry.Wildcard()], [Constraint.GtConst(new ColumnId(0), new Value(2))]);
            qb.MarkUsed(defined);
            qb.MarkDefined(defined);
            var constants = qb.Build(new ActionId(0));
            Assert.Equal(
            [
                "SinglePlan",
                "header a0{c0 == 0, c1 == 3}",
                "header a1{c1 == 10}",
                "exists a0",
                "exists a1",
                "exists a2{c0 > 2}",
            ], PlanLines.Of(QueryPlanner.PlanQuery(constants, db, Enum.Parse<PlanStrategy>(strategy), false)));
        }

        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void AllConstant(string strategy)
        {
            // egglog: Gj and PureSize print the same stage and header; MinCover panics on the variable-free atom.
            // egglog has no exists check: it only checks that the header is non-empty.
            Assert.Equal(
            [
                "SinglePlan",
                "header a0{c0 == 3, c1 == 3}",
                "exists a0",
                "[0] FusedIntersect a1[c0,c1] bind[c0->v0, c1->v1]",
            ], Plan("allconst", Enum.Parse<PlanStrategy>(strategy)));
        }

    }

}
