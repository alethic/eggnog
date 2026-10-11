using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="PlanTemplate.Instantiate{TEvaluator}"/>: egglog's cached-plan instantiation
    /// (core-relations/src/query.rs:133-274), which re-evaluates headers per run and adds extra fast constraints.
    /// </summary>
    public class PlanInstantiationTests
    {

        static (PlanTestDatabase Db, PlanTemplate Plan) ConstRepeat()
        {
            var (db, query) = ExperimentQueries.Build("const_repeat", PlanStrategy.Gj);
            return (db, QueryPlanner.PlanQuery(query, db));
        }

        [Fact]
        public void HeadersAreEvaluatedPerRun()
        {
            var (db, plan) = ConstRepeat();
            using var instance = plan.Instantiate(ref db, [], new ActionId(5));
            Assert.NotNull(instance);
            Assert.Same(plan, instance.Template);
            Assert.Equal(new ActionId(5), instance.Action);
            var header = Assert.Single(instance.Headers);
            Assert.Equal("a0{c1 == 3}", header.ToString());

            // rows(100, 30): only i = 0 has (7i + 3) % 101 == 3.
            Assert.Equal(1, instance.HeaderSubsets[0].Size);
            var rows = new System.Collections.Generic.List<int>();
            foreach (var row in instance.HeaderSubsets[0])
                rows.Add(row);
            Assert.Equal([0], rows);
        }

        [Fact]
        public void ExtraConstraintsComeFirst()
        {
            var (db, plan) = ConstRepeat();
            using var instance = plan.Instantiate(ref db, [(new AtomId(2), Constraint.EqConst(new ColumnId(0), new Value(4)))], new ActionId(0));
            Assert.NotNull(instance);
            Assert.Equal(["a2{c0 == 4}", "a0{c1 == 3}"], instance.Headers.Select(h => h.ToString()));
            var expected = db[new TableId(2)].Rows.Count(r => r[0] == 4);
            Assert.Equal(expected, instance.HeaderSubsets[0].Size);
        }

        [Fact]
        public void EmptyHeaderSkipsTheRun()
        {
            var (db, plan) = ConstRepeat();
            Assert.Null(plan.Instantiate(ref db, [(new AtomId(2), Constraint.EqConst(new ColumnId(0), new Value(999)))], new ActionId(0)));

            // allconst's header R(3, 3) matches no row.
            var (db2, query) = ExperimentQueries.Build("allconst", PlanStrategy.Gj);
            var plan2 = QueryPlanner.PlanQuery(query, db2);
            Assert.Null(plan2.Instantiate(ref db2, [], new ActionId(0)));
        }

        [Fact]
        public void SlowConstraintsAreRejected()
        {
            // c1 of table 2 has no sort column and GtConst is not an equality: it stays slow, which a cached plan
            // cannot take (egglog panics, query.rs:140-145).
            var (db, plan) = ConstRepeat();
            Assert.Throws<InvalidOperationException>(() => plan.Instantiate(ref db, [(new AtomId(2), Constraint.GtConst(new ColumnId(1), new Value(4)))], new ActionId(0)));
            Assert.Throws<ArgumentException>(() => plan.Instantiate(ref db, [(new AtomId(7), Constraint.EqConst(new ColumnId(0), new Value(4)))], new ActionId(0)));
        }

        [Fact]
        public void DecomposedPlansInstantiateToo()
        {
            var (db, query) = ExperimentQueries.Build("path4", PlanStrategy.Gj);
            var plan = QueryPlanner.PlanQuery(query, db);
            using var instance = plan.Instantiate(ref db, [(new AtomId(0), Constraint.EqConst(new ColumnId(0), new Value(1)))], new ActionId(1));
            Assert.NotNull(instance);
            Assert.Equal(["a0{c0 == 1}"], instance.Headers.Select(h => h.ToString()));
        }

    }

}
