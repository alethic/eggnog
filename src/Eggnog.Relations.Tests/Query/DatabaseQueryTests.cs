using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the database's planner interfaces (<see cref="IQuerySchema"/>, <see cref="IPlanStatistics"/>,
    /// <see cref="IHeaderEvaluator"/>) and its rule-set entry points: egglog's <c>process_constraints</c>,
    /// <c>approx_size</c> and <c>col_uniqueness</c> as the planner sees them.
    /// </summary>
    public class DatabaseQueryTests
    {

        static MathEgraph Filled(bool rebuildIds = false)
        {
            var m = new MathEgraph(rebuildIds);
            DatabaseTestUtil.Stage(m.Db, m.Add, [1, 2, 10, 0], [2, 3, 11, 0], [3, 3, 12, 1], [5, 3, 13, 2]);
            DatabaseTestUtil.Stage(m.Db, m.Num, [7, 10, 0], [8, 11, 1]);
            m.Db.MergeAll();
            return m;
        }

        [Fact]
        public void SchemaDescribesTheTables()
        {
            var m = Filled();
            IQuerySchema schema = m.Db;
            Assert.Equal((4, 2), (schema.GetArity(m.Add), schema.GetKeyCount(m.Add)));
            Assert.Equal((3, 1), (schema.GetArity(m.Uf), schema.GetKeyCount(m.Uf)));
            Assert.Throws<KeyNotFoundException>(() => schema.GetArity(new TableId(17)));
        }

        [Fact]
        public void SplitFollowsProcessConstraints()
        {
            var m = Filled();
            IQuerySchema schema = m.Db;
            Constraint[] cs = [Constraint.EqConst(C(1), V(3)), Constraint.Eq(C(0), C(1)), Constraint.GeConst(C(3), V(1)), Constraint.LtConst(C(0), V(9))];
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            schema.SplitConstraints(m.Add, cs, fast, slow);

            // The table's own fast constraints first, then promoted equalities; the rest slow, in order.
            Assert.Equal(["c3 >= 1", "c1 == 3"], fast.Select(c => c.ToString()));
            Assert.Equal(["c0 == c1", "c0 < 9"], slow.Select(c => c.ToString()));

            var fast2 = new List<Constraint>();
            var slow2 = new List<Constraint>();
            using var processed = m.Db.ProcessConstraints(m.Add, cs);
            Assert.Equal(processed.Fast, fast.ToArray());
            Assert.Equal(processed.Slow, slow.ToArray());

            // Appends to what the lists hold.
            fast2.Add(Constraint.EqConst(C(0), V(0)));
            schema.SplitConstraints(m.Uf, [Constraint.EqConst(C(1), V(4))], fast2, slow2);
            Assert.Single(fast2);
            Assert.Equal(["c1 == 4"], slow2.Select(c => c.ToString()));
        }

        [Fact]
        public void ApproximateSizesCountTheFastSubset()
        {
            var m = Filled();
            IPlanStatistics stats = m.Db;
            var all = new QueryAtom(m.Add, new VarColumnMap(), [], []);
            var recent = new QueryAtom(m.Add, new VarColumnMap(), [Constraint.GeConst(C(3), V(1))], []);
            var three = new QueryAtom(m.Add, new VarColumnMap(), [Constraint.EqConst(C(1), V(3))], [Constraint.GtConst(C(0), V(2))]);
            Assert.Equal(4, stats.GetApproximateSize(new AtomId(0), all));
            Assert.Equal(2, stats.GetApproximateSize(new AtomId(0), recent));

            // Slow constraints do not narrow the estimate.
            Assert.Equal(3, stats.GetApproximateSize(new AtomId(0), three));
        }

        [Fact]
        public void ColumnUniquenessComesFromTheColumnIndex()
        {
            var m = Filled();
            IPlanStatistics stats = m.Db;
            var u = stats.GetColumnUniqueness(m.Add, C(1));
            Assert.Equal((4ul, 2ul), (u.TableSize, u.ColumnSize));
            var expected = new ColumnCardEstimator(m.Db).GetColumnUniqueness(m.Add, C(0));
            var actual = stats.GetColumnUniqueness(m.Add, C(0));
            Assert.Equal((expected.TableSize, expected.ColumnSize), (actual.TableSize, actual.ColumnSize));
        }

        [Fact]
        public void HeaderEvaluationIsProcessConstraints()
        {
            var m = Filled();
            IHeaderEvaluator evaluator = m.Db;
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            var subset = evaluator.Evaluate(m.Add, [Constraint.GeConst(C(3), V(1)), Constraint.EqConst(C(1), V(3))], fast, slow);
            Assert.Equal(2, subset.Size);
            Assert.Equal(2, fast.Count);
            Assert.Empty(slow);
            subset.Dispose();

            var none = evaluator.Evaluate(m.Add, [Constraint.EqConst(C(0), V(99))], fast, slow);
            Assert.Equal(0, none.Size);
        }

        [Fact]
        public void FunctionalDependenciesFollowTheKeyColumns()
        {
            var m = Filled();
            var qb = new QueryBuilder(m.Db);
            var x = qb.NewVariable();
            var y = qb.NewVariable();
            var z = qb.NewVariable();
            var t = qb.NewVariable();
            qb.AddAtom(m.Add, [x, y, z, t]);
            qb.AddAtom(m.Num, [V(7), z, t]);
            qb.AddAtom(m.Uf, [z, x, QueryEntry.Wildcard()]);
            var query = qb.Build(new ActionId(0));

            // add: (x, y) -> (z, t); num's key is a constant, so no dependency; uf: z -> x (the wildcard is dropped).
            Assert.Equal(2, query.FunDeps.Count);
            Assert.Equal([x, y], query.FunDeps[0].Antecedent);
            Assert.Equal([z, t], query.FunDeps[0].Consequent);
            Assert.Equal([z], query.FunDeps[1].Antecedent);
            Assert.Equal([x], query.FunDeps[1].Consequent);
        }

        [Fact]
        public void PlanQueryUsesTheDatabaseAsStatistics()
        {
            var m = Filled();
            var qb = new QueryBuilder(m.Db);
            var x = qb.NewVariable();
            var i = qb.NewVariable();
            qb.AddAtom(m.Add, [x, QueryEntry.Wildcard(), i, QueryEntry.Wildcard()]);
            qb.AddAtom(m.Num, [QueryEntry.Wildcard(), i, QueryEntry.Wildcard()]);
            qb.MarkUsed(x);
            var plan = m.Db.PlanQuery(qb.Build(new ActionId(3)));
            Assert.Equal(new ActionId(3), plan.Action);
            Assert.Equal(PlanKind.Single, plan.Kind);
            var evaluator = m.Db;
            using var instance = plan.Instantiate(ref evaluator, default, new ActionId(0));
            Assert.NotNull(instance);
            Assert.Empty(instance.Headers);
        }

        [Fact]
        public void IdColumnsAreTheRebuildColumns()
        {
            var plain = Filled();
            Assert.True(plain.Db.GetIdColumns(plain.Add).IsEmpty);
            var m = Filled(rebuildIds: true);
            Assert.Equal([C(0), C(1), C(2)], m.Db.GetIdColumns(m.Add).ToArray());
            Assert.Equal([C(1)], m.Db.GetIdColumns(m.Num).ToArray());
            Assert.True(m.Db.GetIdColumns(m.Uf).IsEmpty);
        }

        [Fact]
        public void NewRuleSetBuildsOverTheDatabase()
        {
            var m = Filled();
            var rsb = m.Db.NewRuleSet();
            Assert.Same(m.Db, rsb.Database);
            Assert.Equal(4, rsb.EstimateSize(m.Add));
            Assert.Equal(2, rsb.EstimateSize(m.Add, Constraint.GeConst(C(3), V(1))));
            Assert.Equal(1, rsb.EstimateSize(m.Add, Constraint.GtConst(C(0), V(3))));
            using var rs = rsb.Build();
            Assert.True(rs.IsEmpty);
            Assert.Equal(0, rs.Rules.Length);
        }

    }

}
