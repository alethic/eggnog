using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="RuleBuilder"/>: egglog's <c>RuleBuilder</c> (core-relations/src/query.rs:514-874), the
    /// instructions each method emits, the variables it marks used and defined, its validation and its atomicity.
    /// </summary>
    public class RuleBuilderTests
    {

        [Fact]
        public void EveryMethodEmitsItsInstruction()
        {
            var fx = new RuleBuilderFixture();
            var rsb = fx.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            var unused = q.NewVariable("unused");
            q.AddAtom(fx.F, [x, y]);
            q.AddAtom(fx.G, [x, z, unused]);
            var rb = q.Build();

            var c = rb.ReadCounter(fx.Counter);
            var l = rb.Lookup(fx.F, [y], C(1));
            var d = rb.LookupWithDefault(fx.F, [z], V(9), C(1));
            var o = rb.LookupOrInsert(fx.F, [l], [WriteVal.FromCounter(fx.Counter)], C(1));
            rb.Insert(fx.G, [x, o, c]);
            rb.InsertIfEq(fx.G, d, V(4), [x, x, x]);
            rb.Remove(fx.F, [V(3)]);
            var e = rb.CallExternal(fx.Ext, [x]);
            var lf = rb.LookupWithFallback(fx.F, [e], C(1), fx.Fallback, [e, V(1)]);
            var ef = rb.CallExternalWithFallback(fx.Ext, [lf], fx.Fallback, []);
            rb.AssertEq(ef, ef);
            rb.AssertNe(x, V(0));
            rb.AssertAnyNe([x, y], [V(1), d]);
            Assert.Equal(13, rb.InstrCount);
            rb.Build("all");
            using var rs = rsb.Build();

            // Each destination is the next variable, in call order.
            Assert.Equal(Enumerable.Range(4, 7).Select(i => new Variable(i)), new[] { c, l, d, o, e, lf, ef });
            Assert.Equal(
                [
                    "v4 <- ReadCounter ctr0",
                    "v5 <- Lookup t0 [v1] c1",
                    "v6 <- LookupWithDefault t0 [v2] c1 ?? 9",
                    "v7 <- LookupOrInsertDefault t0 [v5] [fresh(ctr0)] c1",
                    "Insert t1 [v0, v7, v4]",
                    "InsertIfEq t1 v6 == 4 [v0, v0, v0]",
                    "Remove t0 [3]",
                    "v8 <- External f0 [v0]",
                    "v9 <- LookupWithFallback t0 [v8] c1 ?? f1 [v8, 1]",
                    "v10 <- ExternalWithFallback f0 [v9] ?? f1 []",
                    "AssertEq v10 v10",
                    "AssertNe v0 0",
                    "AssertAnyNe [v0, v1] [1, v6]",
                ],
                Instrs(rs.GetAction(new ActionId(0))));

            // Read and not defined: x, y, z (the query variables the program reads); never `unused`, nor any
            // destination.
            Assert.Equal([x, y, z], rs.GetAction(new ActionId(0)).UsedVariables);
        }

        [Fact]
        public void KeyArityIsChecked()
        {
            var fx = new RuleBuilderFixture();
            var rb = OneAtomRule(fx, out var x);
            var e = Assert.Throws<QueryException>(() => rb.Lookup(fx.G, [x], C(2)));
            Assert.Equal(QueryErrorKind.KeyArityMismatch, e.Kind);
            Assert.Equal((2, 1), (e.Expected, e.Actual));
            Assert.Equal("table t1 has 2 keys but got 1", e.Message);
            Assert.Equal(QueryErrorKind.KeyArityMismatch, Assert.Throws<QueryException>(() => rb.LookupWithDefault(fx.F, [x, x], x, C(1))).Kind);
            Assert.Equal(QueryErrorKind.KeyArityMismatch, Assert.Throws<QueryException>(() => rb.LookupOrInsert(fx.F, [], [x], C(1))).Kind);
            Assert.Equal(QueryErrorKind.KeyArityMismatch, Assert.Throws<QueryException>(() => rb.LookupWithFallback(fx.G, [x, x, x], C(0), fx.Ext, [])).Kind);
            Assert.Equal(QueryErrorKind.KeyArityMismatch, Assert.Throws<QueryException>(() => rb.Remove(fx.F, [])).Kind);
            AssertUntouched(rb, x);
        }

        [Fact]
        public void RowArityIsChecked()
        {
            var fx = new RuleBuilderFixture();
            var rb = OneAtomRule(fx, out var x);
            var e = Assert.Throws<QueryException>(() => rb.Insert(fx.G, [x, x]));
            Assert.Equal(QueryErrorKind.TableArityMismatch, e.Kind);
            Assert.Equal((3, 2), (e.Expected, e.Actual));
            Assert.Equal(QueryErrorKind.TableArityMismatch, Assert.Throws<QueryException>(() => rb.InsertIfEq(fx.F, x, x, [x, x, x])).Kind);
            AssertUntouched(rb, x);
        }

        [Fact]
        public void DefaultsMustFillTheValueColumns()
        {
            var fx = new RuleBuilderFixture();
            var rb = OneAtomRule(fx, out var x);

            // Too many: egglog's validate_vals reports the first column beyond the arity.
            var many = Assert.Throws<QueryException>(() => rb.LookupOrInsert(fx.G, [x, x], [x, x], C(2)));
            Assert.Equal(QueryErrorKind.TableArityMismatch, many.Kind);
            Assert.Equal((3, 3), (many.Expected, many.Actual));

            // Too few: egglog would stage a short row; rejected here with the row length the defaults reach.
            var few = Assert.Throws<QueryException>(() => rb.LookupOrInsert(fx.G, [x, x], [], C(1)));
            Assert.Equal((3, 2), (few.Expected, few.Actual));

            // A current-value default must copy an earlier cell (the instruction's own check, before anything changes).
            Assert.Throws<ArgumentOutOfRangeException>(() => rb.LookupOrInsert(fx.F, [x], [WriteVal.CurrentValue(1)], C(1)));
            AssertUntouched(rb, x);
            Assert.Equal(new Variable(2), rb.LookupOrInsert(fx.F, [x], [WriteVal.CurrentValue(0)], C(1)));
        }

        [Fact]
        public void DestinationColumnsMustExist()
        {
            var fx = new RuleBuilderFixture();
            var rb = OneAtomRule(fx, out var x);
            Assert.Throws<ArgumentOutOfRangeException>(() => rb.Lookup(fx.F, [x], C(2)));
            Assert.Throws<ArgumentOutOfRangeException>(() => rb.LookupWithDefault(fx.F, [x], x, C(5)));
            Assert.Throws<ArgumentOutOfRangeException>(() => rb.LookupOrInsert(fx.F, [x], [x], C(2)));
            Assert.Throws<ArgumentOutOfRangeException>(() => rb.LookupWithFallback(fx.F, [x], C(-1), fx.Ext, []));
            AssertUntouched(rb, x);
        }

        [Fact]
        public void ComparedListsMustHaveEqualLengths()
        {
            var fx = new RuleBuilderFixture();
            var rb = OneAtomRule(fx, out var x);
            var e = Assert.Throws<QueryException>(() => rb.AssertAnyNe([x, x], [x]));
            Assert.Equal(QueryErrorKind.MultiComparisonMismatch, e.Kind);
            Assert.Null(e.Table);
            Assert.Equal((2, 1), (e.Expected, e.Actual));
            Assert.Equal("attempt to compare two groups of values, one of length 2, another of length 1", e.Message);
            AssertUntouched(rb, x);
        }

        [Fact]
        public void OperandsMustBeVariablesOfTheRuleOrConstants()
        {
            var fx = new RuleBuilderFixture();
            var rb = OneAtomRule(fx, out var x);
            var foreign = new Variable(7);
            Assert.Throws<ArgumentException>(() => rb.Insert(fx.F, [x, foreign]));
            Assert.Throws<ArgumentException>(() => rb.Insert(fx.F, [x, QueryEntry.Wildcard()]));
            Assert.Throws<ArgumentException>(() => rb.CallExternal(fx.Ext, [foreign]));
            Assert.Throws<ArgumentException>(() => rb.CallExternal(fx.Ext, [new Variable(1)]));
            Assert.Throws<ArgumentException>(() => rb.AssertEq(x, foreign));
            Assert.Throws<ArgumentException>(() => rb.LookupOrInsert(fx.F, [x], [foreign], C(1)));
            Assert.Throws<ArgumentException>(() => rb.LookupWithFallback(fx.F, [x], C(1), fx.Ext, [foreign]));
            Assert.Throws<ArgumentException>(() => rb.CallExternalWithFallback(fx.Ext, [x], fx.Ext, [foreign]));
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => rb.Insert(new TableId(9), [x]));
            AssertUntouched(rb, x);
        }

        [Fact]
        public void ReadingAnUnboundVariableFailsTheBuild()
        {
            var fx = new RuleBuilderFixture();
            var rsb = fx.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable();
            var free = q.NewVariable("free");
            q.AddAtom(fx.F, [x, QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.Insert(fx.F, [x, free]);
            var e = Assert.Throws<InvalidOperationException>(() => rb.Build());
            Assert.Contains("v1", e.Message);

            // The rule set is unchanged and the failed rule is closed.
            Assert.Equal(0, rsb.Count);
            Assert.Throws<InvalidOperationException>(() => rb.Insert(fx.F, [x, x]));
            using var rs = rsb.Build();
            Assert.True(rs.IsEmpty);
        }

        [Fact]
        public void BuildWithoutDescriptionUsesTheEmptyString()
        {
            var fx = new RuleBuilderFixture();
            var rsb = fx.Db.NewRuleSet();
            var rb = rsb.NewRule().Build();
            Assert.Throws<ArgumentNullException>(() => rb.Build(null!));
            var rule = rb.Build();
            using var rs = rsb.Build();
            Assert.Equal(string.Empty, rs[rule].Description);
        }

        [Fact]
        public void ARuleWithoutAtomsRunsOnce()
        {
            var fx = new RuleBuilderFixture();
            var rsb = fx.Db.NewRuleSet();
            var rb = rsb.NewRule().Build();
            var fresh = rb.ReadCounter(fx.Counter);
            rb.Insert(fx.F, [V(50), fresh]);
            rb.Build();
            using var rs = rsb.Build();

            var plan = rs[new RuleId(0)];
            var single = Assert.IsType<SinglePlan>(plan.Template);
            Assert.Empty(single.Stages);
            Assert.NotNull(plan.Instance);
            Assert.Empty(rs.GetAction(plan.Action).UsedVariables);
            var (changed, matches) = NaiveRuleRunner.Run(fx.Db, rs);
            Assert.True(changed);
            Assert.Equal(1, matches[0]);
            Assert.Contains("[50, 0]", Rows(fx.Db, fx.F));
        }

        [Fact]
        public void AnEmptyHeaderStillAddsTheRule()
        {
            var fx = new RuleBuilderFixture();
            var rsb = fx.Db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable();
            q.AddAtom(fx.F, [x, V(999)]);
            var rb = q.Build();
            rb.Insert(fx.F, [x, x]);
            var empty = rb.Build("never");
            var q2 = rsb.NewRule();
            var y = q2.NewVariable();
            q2.AddAtom(fx.F, [y, V(10)]);
            var rb2 = q2.Build();
            rb2.Insert(fx.G, [y, y, y]);
            var next = rb2.Build("once");
            using var rs = rsb.Build();

            // egglog keeps the rule (its executor skips the empty header); the bijection holds for the next one.
            Assert.Equal((new RuleId(0), new RuleId(1)), (empty, next));
            Assert.True(rs[empty].IsEmpty);
            Assert.Null(rs[empty].Instance);
            Assert.Equal(new ActionId(0), rs[empty].Action);
            Assert.False(rs[next].IsEmpty);
            Assert.Equal(new ActionId(1), rs[next].Action);
            Assert.Contains("(empty)", rs[empty].ToString());

            var (_, matches) = NaiveRuleRunner.Run(fx.Db, rs);
            Assert.Equal([0, 1], matches);
            Assert.Contains("[1, 1, 1]", Rows(fx.Db, fx.G));
        }

        static RuleBuilder OneAtomRule(RuleBuilderFixture fx, out Variable x)
        {
            var q = fx.Db.NewRuleSet().NewRule();
            x = q.NewVariable();
            q.AddAtom(fx.F, [x, QueryEntry.Wildcard()]);
            return q.Build();
        }

        /// <summary>
        /// Checks that failed calls left the rule as built by <see cref="OneAtomRule"/>: no instruction and no variable
        /// beyond <paramref name="x"/> (the next destination is v1).
        /// </summary>
        static void AssertUntouched(RuleBuilder rb, Variable x)
        {
            Assert.Equal(0, rb.InstrCount);
            Assert.Equal(new Variable(x.Index + 1), rb.ReadCounter(new CounterId(0)));
        }

    }

}
