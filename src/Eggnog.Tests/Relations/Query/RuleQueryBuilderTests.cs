using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="RuleQueryBuilder"/>: egglog's <c>QueryBuilder</c> (core-relations/src/query.rs:282-464) over
    /// a real <see cref="Database"/>.
    /// </summary>
    public class RuleQueryBuilderTests
    {

        static MathEgraph Filled(bool rebuildIds = false)
        {
            var m = new MathEgraph(rebuildIds);
            DatabaseTestUtil.Stage(m.Db, m.Add, [1, 2, 10, 0], [2, 3, 11, 0], [3, 3, 12, 1], [5, 3, 13, 2]);
            DatabaseTestUtil.Stage(m.Db, m.Num, [7, 10, 0], [8, 11, 1]);
            m.Db.MergeAll();
            return m;
        }

        static (RuleSet Rules, RulePlan Plan) BuildOne(RuleSetBuilder rsb, RuleQueryBuilder q)
        {
            q.Build().Build();
            var rs = rsb.Build();
            return (rs, rs[new RuleId(0)]);
        }

        [Fact]
        public void ConstantsBecomeEqualitiesSplitByTheDatabase()
        {
            var m = Filled();
            var rsb = m.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var z = q.NewVariable("z");
            var t = q.NewVariable("t");
            var a = q.AddAtom(m.Add, [x, V(3), z, t], [Constraint.GtConst(C(0), V(1)), Constraint.LtConst(C(3), V(2))]);
            var (rs, plan) = BuildOne(rsb, q);
            using var _ = rs;

            // The range on the sort column is the table's own fast constraint; the constant's equality is promoted
            // through a column index after it; the comparison on another column stays slow.
            var atom = plan.Template.Atoms[a];
            Assert.Equal(["c3 < 2", "c1 == 3"], atom.FastConstraints.Select(c => c.ToString()));
            Assert.Equal(["c0 > 1"], atom.SlowConstraints.Select(c => c.ToString()));
            Assert.NotNull(plan.Instance);
            var header = Assert.Single(plan.Instance.Headers);
            Assert.Equal("a0{c3 < 2, c1 == 3}", header.ToString());

            // Rows with c3 < 2 and c1 == 3: (2, 3, 11, 0) and (3, 3, 12, 1).
            Assert.Equal(2, plan.Instance.HeaderSubsets[0].Size);
        }

        [Fact]
        public void UncacheableEqualitiesStaySlow()
        {
            var m = Filled();
            var rsb = m.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable();
            var t = q.NewVariable();
            var a = q.AddAtom(m.Uf, [x, V(4), t]);
            var b = q.AddAtom(m.Uf, [V(4), x, t]);
            var (rs, plan) = BuildOne(rsb, q);
            using var _ = rs;

            // The union-find's canonical column is uncacheable; its key column is answered by the table itself.
            Assert.Empty(plan.Template.Atoms[a].FastConstraints);
            Assert.Equal(["c1 == 4"], plan.Template.Atoms[a].SlowConstraints.Select(c => c.ToString()));
            Assert.Equal(["c0 == 4"], plan.Template.Atoms[b].FastConstraints.Select(c => c.ToString()));
        }

        [Fact]
        public void RepeatedVariablesChainSlowEqualities()
        {
            var m = Filled();
            var rsb = m.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable();
            var a = q.AddAtom(m.Add, [x, x, x, x]);
            var (rs, plan) = BuildOne(rsb, q);
            using var _ = rs;

            var atom = plan.Template.Atoms[a];
            Assert.Equal(["c1 == c0", "c2 == c1", "c3 == c2"], atom.SlowConstraints.Select(c => c.ToString()));
            Assert.Equal(C(3), atom.GetColumn(x));
        }

        [Fact]
        public void WildcardsBindNothing()
        {
            var m = Filled();
            var rsb = m.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable();
            var a = q.AddAtom(m.Add, [QueryEntry.Wildcard(), x, QueryEntry.Wildcard(), QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.Insert(m.Num, [x, x, V(9)]);
            rb.Build();
            using var rs = rsb.Build();

            var atom = rs[new RuleId(0)].Template.Atoms[a];
            Assert.Equal(1, atom.VarColumns.ColumnCount);
            Assert.True(atom.VarColumns.TryGetVariable(C(1), out var bound));
            Assert.Equal(x, bound);
            Assert.Empty(atom.SlowConstraints);
            Assert.Empty(atom.FastConstraints);
            Assert.Equal([x], rs.GetAction(new ActionId(0)).UsedVariables);
        }

        [Fact]
        public void BadArityReportsEgglogsError()
        {
            var m = Filled();
            var q = m.Db.NewRuleSet().NewRule();
            var x = q.NewVariable();
            var e = Assert.Throws<QueryException>(() => q.AddAtom(m.Num, [x, x]));
            Assert.Equal(QueryErrorKind.BadArity, e.Kind);
            Assert.Equal(m.Num, e.Table);
            Assert.Equal(3, e.Expected);
            Assert.Equal(2, e.Actual);
            Assert.Equal("table t1 expected 3 columns but got 2", e.Message);

            // Nothing was added: the next atom is still a0.
            Assert.Equal(new AtomId(0), q.AddAtom(m.Num, [x, x, x]));
        }

        [Fact]
        public void ConstraintsBeyondTheArityAreRejected()
        {
            var m = Filled();
            var q = m.Db.NewRuleSet().NewRule();
            var x = q.NewVariable();
            var bad = Constraint.GeConst(C(3), V(0));
            var e = Assert.Throws<QueryException>(() => q.AddAtom(m.Num, [x, x, x], [bad]));
            Assert.Equal(QueryErrorKind.InvalidConstraint, e.Kind);
            Assert.Equal(bad, e.Constraint);
            Assert.Equal(3, e.Actual);
            Assert.Equal(3, e.Expected);

            var eq = Assert.Throws<QueryException>(() => q.AddAtom(m.Num, [x, x, x], [Constraint.Eq(C(0), C(7))]));
            Assert.Equal(7, eq.Actual);
            Assert.IsAssignableFrom<ArgumentException>(eq);
            Assert.Equal(new AtomId(0), q.AddAtom(m.Num, [x, x, x], [Constraint.Eq(C(0), C(2))]));
        }

        [Fact]
        public void UnknownVariablesAndTablesAreRejected()
        {
            var m = Filled();
            var rsb = m.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable();
            var ex = Assert.Throws<ArgumentException>(() => q.AddAtom(m.Num, [x, new Variable(5), x]));
            Assert.IsNotType<QueryException>(ex);
            Assert.Throws<KeyNotFoundException>(() => q.AddAtom(new TableId(42), [x]));
            Assert.Throws<ArgumentOutOfRangeException>(() => q.Strategy = (PlanStrategy)9);
        }

        [Fact]
        public void StrategyReachesThePlan()
        {
            var m = Filled();
            var rsb = m.Db.NewRuleSet();
            foreach (var strategy in new[] { PlanStrategy.Gj, PlanStrategy.MinCover })
            {
                var q = rsb.NewRule();
                q.Strategy = strategy;
                Assert.Equal(strategy, q.Strategy);
                var x = q.NewVariable();
                var y = q.NewVariable();
                var z = q.NewVariable();
                q.AddAtom(m.Add, [x, y, z, QueryEntry.Wildcard()]);
                q.AddAtom(m.Num, [QueryEntry.Wildcard(), z, QueryEntry.Wildcard()]);
                var rb = q.Build();
                rb.Insert(m.Num, [x, y, z]);
                rb.Build();
            }

            using var rs = rsb.Build();
            var gj = Assert.IsType<SinglePlan>(rs[new RuleId(0)].Template);
            var fj = Assert.IsType<SinglePlan>(rs[new RuleId(1)].Template);

            // Generic Join intersects z across both atoms; Free Join covers whole atoms and probes.
            Assert.Contains(gj.Stages, s => s.Kind == JoinStageKind.Intersect);
            Assert.DoesNotContain(fj.Stages, s => s.Kind == JoinStageKind.Intersect);
        }

        [Fact]
        public void NoDecompositionKeepsOneBag()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var edges = db.AddTable(Strict(2, 2));
            DatabaseTestUtil.Stage(db, edges, Enumerable.Range(0, 40).Select(i => new[] { (uint)(i % 7), (uint)((i * 7 + 3) % 41) }).ToArray());
            db.MergeAll();
            var kinds = new List<PlanKind>();
            foreach (var noDecomposition in new[] { false, true })
            {
                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                q.NoDecomposition = noDecomposition;
                Assert.Equal(noDecomposition, q.NoDecomposition);

                // path5: a -> b -> c -> d -> e -> f, reading only the ends.
                var v = Enumerable.Range(0, 6).Select(i => q.NewVariable(((char)('a' + i)).ToString())).ToArray();
                for (var i = 0; i < 5; i++)
                    q.AddAtom(edges, [v[i], v[i + 1]]);

                var rb = q.Build();
                rb.Insert(edges, [v[0], v[5]]);
                rb.Build();
                using var rs = rsb.Build();
                kinds.Add(rs[new RuleId(0)].Template.Kind);
            }

            Assert.Equal([PlanKind.Decomposed, PlanKind.Single], kinds);
        }

        [Fact]
        public void IdConstantsAreRecorded()
        {
            var m = Filled(rebuildIds: true);
            var rsb = m.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable();
            var y = q.NewVariable();
            var t = q.NewVariable();

            // add's id columns are c0..c2; c3 is the timestamp. num's id column is c1; c0 holds a number.
            var a0 = q.AddAtom(m.Add, [V(3), x, y, t], [Constraint.EqConst(C(1), V(3)), Constraint.EqConst(C(3), V(1)), Constraint.GtConst(C(2), V(0))]);
            var a1 = q.AddAtom(m.Num, [V(8), y, QueryEntry.Wildcard()]);
            var a2 = q.AddAtom(m.Num, [x, V(11), t]);
            var a3 = q.AddAtom(m.Uf, [V(11), y, t]);
            var rb = q.Build();
            rb.Insert(m.Num, [V(11), V(11), V(5)]);
            rb.Build();
            using var rs = rsb.Build();

            var expected = new[]
            {
                new IdConstant(a0, C(1), V(3)),
                new IdConstant(a0, C(0), V(3)),
                new IdConstant(a2, C(1), V(11)),
            };
            Assert.Equal(expected, rs[new RuleId(0)].IdConstants);
            Assert.Equal("a0.c1 == 3", expected[0].ToString());
            Assert.Equal(expected, rs.BuildCachedPlan(new RuleId(0)).IdConstants);
            _ = a1;
            _ = a3;
        }

        [Fact]
        public void TablesWithoutRebuildColumnsRecordNoIdConstants()
        {
            var m = Filled();
            var rsb = m.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable();
            q.AddAtom(m.Add, [V(3), x, V(12), QueryEntry.Wildcard()]);
            var (rs, plan) = BuildOne(rsb, q);
            using var _ = rs;
            Assert.Empty(plan.IdConstants);
        }

        [Fact]
        public void BuildHandsOverAndCloses()
        {
            var m = Filled();
            var rsb = m.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable();
            q.AddAtom(m.Num, [x, x, x]);
            var rb = q.Build();
            Assert.Throws<InvalidOperationException>(() => q.NewVariable());
            Assert.Throws<InvalidOperationException>(() => q.AddAtom(m.Num, [x, x, x]));
            Assert.Throws<InvalidOperationException>(() => q.Strategy = PlanStrategy.MinCover);
            Assert.Throws<InvalidOperationException>(() => q.NoDecomposition = true);
            Assert.Throws<InvalidOperationException>(() => q.Build());
            rb.Build();
            Assert.Throws<InvalidOperationException>(() => rb.Build());
            using var rs = rsb.Build();
            Assert.Equal(1, rs.Count);
        }

        [Fact]
        public void StartingAnotherRuleAbandonsTheOpenOne()
        {
            var m = Filled();
            var rsb = m.Db.NewRuleSet();
            var first = rsb.NewRule();
            var x = first.NewVariable();
            first.AddAtom(m.Num, [x, x, x]);
            var second = rsb.NewRule();
            Assert.Throws<InvalidOperationException>(() => first.NewVariable());
            Assert.Throws<InvalidOperationException>(() => first.Build());

            // The abandoned rule added nothing; its variables do not leak into the next rule.
            Assert.Equal(new Variable(0), second.NewVariable());
            var rb = second.Build();
            var third = rsb.NewRule();
            Assert.Throws<InvalidOperationException>(() => rb.Build());
            third.Build().Build();
            using var rs = rsb.Build();
            Assert.Equal(1, rs.Count);
            Assert.Throws<InvalidOperationException>(() => third.NewVariable());
            Assert.Throws<InvalidOperationException>(() => rsb.NewRule());
            Assert.Throws<InvalidOperationException>(() => rsb.Build());
        }

    }

}
