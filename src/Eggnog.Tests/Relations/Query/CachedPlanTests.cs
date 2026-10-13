using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="CachedPlan"/>, <see cref="RuleSet.BuildCachedPlan"/> and
    /// <see cref="RuleSetBuilder.AddRuleFromCachedPlan"/>: egglog's plan-once, instantiate-per-run path
    /// (core-relations/src/query.rs:66-79, 133-274) as egglog-bridge uses it for semi-naive evaluation
    /// (egglog-bridge/src/rule.rs:779-846).
    /// </summary>
    public class CachedPlanTests
    {

        /// <summary>
        /// Builds <c>add(x, y, i, t1), num(n, i, t2) =&gt; num(n, x, next)</c> with a fixed filter on the num atom's
        /// value, and caches it.
        /// </summary>
        static (MathEgraph M, CachedPlan Cached, RuleSet Original) Cache()
        {
            var m = new MathEgraph();
            DatabaseTestUtil.Stage(m.Db, m.Add, [1, 2, 10, 0], [2, 3, 11, 0]);
            DatabaseTestUtil.Stage(m.Db, m.Num, [7, 10, 0], [8, 11, 0], [9, 12, 0]);
            m.Db.MergeAll();

            var rsb = m.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var i = q.NewVariable("i");
            var n = q.NewVariable("n");
            q.AddAtom(m.Add, [x, y, i, QueryEntry.Wildcard()]);
            q.AddAtom(m.Num, [n, i, QueryEntry.Wildcard()], [Constraint.LeConst(C(2), V(5))]);
            var rb = q.Build();
            rb.Insert(m.Num, [n, x, V(1)]);
            var rule = rb.Build("cached");
            var rs = rsb.Build();
            return (m, rs.BuildCachedPlan(rule), rs);
        }

        [Fact]
        public void BuildCachedPlanSharesTheRule()
        {
            var (_, cached, rs) = Cache();
            using var _ = rs;
            var plan = rs[new RuleId(0)];
            Assert.Same(plan.Template, cached.Plan);
            Assert.Same(rs.GetAction(plan.Action), cached.Actions);
            Assert.Same(plan.Symbols, cached.Symbols);
            Assert.Equal("cached", cached.Description);
            Assert.Empty(cached.ExtraConstraints);
            Assert.Throws<ArgumentOutOfRangeException>(() => rs.BuildCachedPlan(new RuleId(1)));
        }

        [Fact]
        public void ExtraConstraintsComeFirstAndHeadersAreReevaluated()
        {
            var (m, cached, original) = Cache();
            original.Dispose();
            var rsb = m.Db.NewRuleSet();
            var rule = rsb.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.GeConst(C(3), V(0)))]);
            Assert.Equal(new RuleId(0), rule);
            using var first = rsb.Build();
            var instance = first[new RuleId(0)].Instance;
            Assert.NotNull(instance);
            Assert.Equal(["a0{c3 >= 0}", "a1{c2 <= 5}"], instance.Headers.Select(h => h.ToString()));
            Assert.Equal([2, 3], instance.HeaderSubsets.Select(s => s.Size));
            Assert.Equal([(new AtomId(0), Constraint.GeConst(C(3), V(0)))], first[new RuleId(0)].ExtraConstraints);

            // The tables grow; a new instantiation sees the new rows, the stages are not re-planned.
            DatabaseTestUtil.Stage(m.Db, m.Add, [3, 3, 12, 1]);
            DatabaseTestUtil.Stage(m.Db, m.Num, [6, 13, 3]);
            m.Db.MergeAll();
            var rsb2 = m.Db.NewRuleSet();
            rsb2.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.GeConst(C(3), V(0)))]);
            using var second = rsb2.Build();
            var again = second[new RuleId(0)];
            Assert.Same(cached.Plan, again.Template);
            Assert.Equal([3, 4], again.Instance!.HeaderSubsets.Select(s => s.Size));
        }

        [Fact]
        public void AnEmptyHeaderAddsNothing()
        {
            var (m, cached, original) = Cache();
            original.Dispose();
            var rsb = m.Db.NewRuleSet();

            // No add row is newer than timestamp 1: the variant is provably empty.
            Assert.Null(rsb.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.GeConst(C(3), V(1)))]));
            Assert.Equal(0, rsb.Count);

            // No action id was taken: the next rule is r0 with action a0.
            var rule = rsb.AddRuleFromCachedPlan(cached);
            Assert.Equal(new RuleId(0), rule);
            using var rs = rsb.Build();
            Assert.Equal(1, rs.Count);
            Assert.Equal(new ActionId(0), rs[new RuleId(0)].Action);

            // The template's own header can empty too, once the table it reads is cleared.
            m.Db.ClearTable(m.Num);
            Assert.Null(m.Db.NewRuleSet().AddRuleFromCachedPlan(cached));
        }

        [Fact]
        public void SlowConstraintsAndUnknownAtomsAreRejected()
        {
            var (m, cached, original) = Cache();
            original.Dispose();
            var rsb = m.Db.NewRuleSet();

            // c1 of add is neither the sort column nor an equality: no fast evaluation.
            var e = Assert.Throws<InvalidOperationException>(() => rsb.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.GtConst(C(1), V(0)))]));
            Assert.Contains("fast pushdown", e.Message);
            Assert.Throws<ArgumentException>(() => rsb.AddRuleFromCachedPlan(cached, [(new AtomId(5), Constraint.GeConst(C(3), V(0)))]));
            Assert.Throws<ArgumentNullException>(() => rsb.AddRuleFromCachedPlan(null!));

            // An equality is promoted through a column index, so it is fine.
            Assert.Equal(new RuleId(0), rsb.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.EqConst(C(1), V(2)))]));
            using var rs = rsb.Build();
            Assert.Equal(1, rs.Count);
        }

        [Fact]
        public void ActionIdsFollowTheNewRuleSet()
        {
            var (m, cached, original) = Cache();
            original.Dispose();
            var rsb = m.Db.NewRuleSet();
            var scratch = rsb.NewRule();
            var v = scratch.NewVariable();
            scratch.AddAtom(m.Num, [v, QueryEntry.Wildcard(), QueryEntry.Wildcard()]);
            scratch.Build().Build();
            var fromCache = rsb.AddRuleFromCachedPlan(cached);
            using var rs = rsb.Build();

            Assert.Equal(new RuleId(1), fromCache);
            var plan = rs[new RuleId(1)];
            Assert.Equal(new ActionId(1), plan.Action);
            Assert.Equal(new ActionId(1), plan.Instance!.Action);

            // The template still names the action of the rule set it was planned in.
            Assert.Equal(new ActionId(0), plan.Template.Action);
            Assert.Same(cached.Actions, rs.GetAction(new ActionId(1)));
        }

        [Fact]
        public void RecachingKeepsTheExtraConstraints()
        {
            var (m, cached, original) = Cache();
            original.Dispose();
            var rsb = m.Db.NewRuleSet();
            rsb.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.GeConst(C(3), V(0)))]);
            using var rs = rsb.Build();
            var recached = rs.BuildCachedPlan(new RuleId(0));
            Assert.Equal([(new AtomId(0), Constraint.GeConst(C(3), V(0)))], recached.ExtraConstraints);

            // egglog's order: this instantiation's extras, then the re-cached rule's headers (its extras first).
            var rsb2 = m.Db.NewRuleSet();
            rsb2.AddRuleFromCachedPlan(recached, [(new AtomId(1), Constraint.LtConst(C(2), V(5)))]);
            using var rs2 = rsb2.Build();
            Assert.Equal(["a1{c2 < 5}", "a0{c3 >= 0}", "a1{c2 <= 5}"], rs2[new RuleId(0)].Instance!.Headers.Select(h => h.ToString()));
            Assert.Equal(2, rs2[new RuleId(0)].ExtraConstraints.Length);
        }

        [Fact]
        public void SeminaiveVariantsSelectOnlyNewMatches()
        {
            // egglog-bridge's variants for a two-atom rule: focus atom i gets ts >= mid, atoms before it ts < mid.
            var m = new MathEgraph();
            var db = m.Db;
            DatabaseTestUtil.Stage(db, m.Add, [1, 2, 10, 0]);
            DatabaseTestUtil.Stage(db, m.Num, [7, 10, 0]);
            db.MergeAll();

            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var i = q.NewVariable("i");
            var n = q.NewVariable("n");
            q.AddAtom(m.Add, [x, QueryEntry.Wildcard(), i, QueryEntry.Wildcard()]);
            q.AddAtom(m.Num, [n, i, QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.Insert(m.Num, [V(100), n, V(9)]);
            var rule = rb.Build();
            using (var built = rsb.Build())
            {
                var cachedPlan = built.BuildCachedPlan(rule);

                RuleId?[] Variants(uint mid)
                {
                    var r = db.NewRuleSet();
                    var ids = new[]
                    {
                        r.AddRuleFromCachedPlan(cachedPlan, [(new AtomId(0), Constraint.GeConst(C(3), V(mid)))]),
                        r.AddRuleFromCachedPlan(cachedPlan, [(new AtomId(1), Constraint.GeConst(C(2), V(mid))), (new AtomId(0), Constraint.LtConst(C(3), V(mid)))]),
                    };
                    using var set = r.Build();
                    return ids;
                }

                // Everything is older than 1: both variants are skipped.
                Assert.Equal([null, null], Variants(1));

                // A new num row at 1 joins the old add row: only the second variant survives.
                DatabaseTestUtil.Stage(db, m.Num, [8, 10, 1]);
                db.MergeAll();
                var r2 = db.NewRuleSet();
                Assert.Null(r2.AddRuleFromCachedPlan(cachedPlan, [(new AtomId(0), Constraint.GeConst(C(3), V(1)))]));
                Assert.Equal(new RuleId(0), r2.AddRuleFromCachedPlan(cachedPlan, [(new AtomId(1), Constraint.GeConst(C(2), V(1))), (new AtomId(0), Constraint.LtConst(C(3), V(1)))]));
                using var set2 = r2.Build();
                var (_, matches) = NaiveRuleRunner.Run(db, set2);
                Assert.Equal([1], matches);
                Assert.Contains("[100, 8, 9]", Rows(db, m.Num));
            }
        }

        [Fact]
        public void OneCachedPlanCanBeAddedTwice()
        {
            var (m, cached, original) = Cache();
            original.Dispose();
            var rsb = m.Db.NewRuleSet();
            var a = rsb.AddRuleFromCachedPlan(cached);
            var b = rsb.AddRuleFromCachedPlan(cached, [(new AtomId(1), Constraint.EqConst(C(0), V(8)))]);
            using var rs = rsb.Build();

            Assert.Equal((new RuleId(0), new RuleId(1)), (a, b));
            Assert.Same(rs.GetAction(new ActionId(0)), rs.GetAction(new ActionId(1)));

            // add(1, 2, 10) with num(7, 10), and add(2, 3, 11) with num(8, 11); the second rule keeps only num 8.
            Assert.Equal(2, NaiveRuleRunner.Enumerate(m.Db, rs[new RuleId(0)]).Bindings.Count);
            Assert.Single(NaiveRuleRunner.Enumerate(m.Db, rs[new RuleId(1)]).Bindings);
        }

    }

}
