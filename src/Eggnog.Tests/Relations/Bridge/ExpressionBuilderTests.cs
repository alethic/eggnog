using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.RuleTerm;
using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the port of egglog-bridge's rule macros (egglog-bridge/src/macros.rs):
    /// <see cref="ExpressionBuilder"/>, <see cref="RuleTerm"/>, <see cref="RuleFact"/> and <see cref="RuleCommand"/>.
    /// </summary>
    public class ExpressionBuilderTests
    {

        [Fact]
        public void LeftHandSidesCreateVariablesAndAtomsInMacroOrder()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var rule = ExpressionBuilder.DefineRule(egraph,
                [Call(fx.Add, "x", Call(fx.Add, "y", "z")).Is("id")],
                [RuleCommand.Set(Call(fx.Add, Call(fx.Add, "x", "y"), "z"), "id")]);

            var info = egraph.GetRuleInfo(rule);
            Assert.True(info.Query.SemiNaive);
            Assert.Equal("((-> (add x (add y z)) id) => (set (add (add x y) z) id))", info.Description);

            // The output first, then the arguments; the nested call is an atom of its own, added first.
            var names = info.Query.Variables.Select(v => v.Name).ToArray();
            Assert.Equal(["id", "x", "y", "z", "(add y z)"], names.Take(5));
            Assert.Equal(2, info.Query.Atoms.Count);
            var inner = info.Query.Atoms[0].Entries;
            var outer = info.Query.Atoms[1].Entries;
            Assert.Equal(["y", "z", "(add y z)"], inner[..3].ToArray().Select(e => e.Variable.Name));
            Assert.Equal(["x", "(add y z)", "id"], outer[..3].ToArray().Select(e => e.Variable.Name));

            // Both atoms match rows that are not subsumed.
            Assert.Equal(RuleEntry.Const(new Value(SchemaMath.NotSubsumed), ColumnTy.Id), inner[4]);
            Assert.Equal(RuleEntry.Const(new Value(SchemaMath.NotSubsumed), ColumnTy.Id), outer[4]);

            // The right-hand side looks up the inner call, then sets the outer row.
            Assert.Equal([RuleActionKind.LookupOrInsert, RuleActionKind.Insert], info.Query.Actions.Select(a => a.Kind));
        }

        [Fact]
        public void VariablesTakeTheTypeOfTheirFirstColumn()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var i64 = egraph.GetFunctionInfo(fx.Num).Schema[0];
            var rule = ExpressionBuilder.DefineRule(egraph,
                [Call(fx.Num, "n").Is("x"), Call(fx.Add, "x", "x").Is("y")],
                [RuleCommand.Union("x", "y")]);

            var vars = egraph.GetRuleInfo(rule).Query.Variables;
            Assert.Equal(new RuleVarInfo(ColumnTy.Id, "x"), vars[0]);
            Assert.Equal(new RuleVarInfo(i64, "n"), vars[1]);
            Assert.Equal(RuleActionKind.Union, egraph.GetRuleInfo(rule).Query.Actions.Single().Kind);
        }

        [Fact]
        public void RightHandSidesEvaluateCallsLetsAndUnions()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var n1 = fx.Number(1);
            var n2 = fx.Number(2);
            egraph.AddTerm(fx.Same, [n1, n2]);
            var two = egraph.BaseValueConstant<Int64Codec, long>(2);

            var rule = ExpressionBuilder.DefineRule(egraph,
                [Call(fx.Same, "a", "b").Is("s")],
                [
                    RuleCommand.Let("t", Call(fx.Add, "a", "b")),
                    RuleCommand.Let("t", Call(fx.Add, "b", "a")),
                    RuleCommand.Union("t", Call(fx.Add, "b", "a")),
                    RuleCommand.Set(Call(fx.Same, "b", "a"), "t"),
                    RuleCommand.Union("s", Call(fx.Num, two)),
                ]);

            Assert.True(egraph.RunRules(rule).Changed);

            // The second let still looks its term up but does not rebind t, so t is add(a, b), unioned with add(b, a).
            var addAB = egraph.LookupId(fx.Add, [n1, n2])!.Value;
            var addBA = egraph.LookupId(fx.Add, [n2, n1])!.Value;
            Assert.Equal(fx.Canon(addAB), fx.Canon(addBA));
            Assert.Equal(fx.Canon(addAB), fx.Canon(egraph.LookupId(fx.Same, [n2, n1])!.Value));
            Assert.Equal(fx.Canon(n2), fx.Canon(egraph.LookupId(fx.Same, [n1, n2])!.Value));
            Assert.Equal(
                [RuleActionKind.LookupOrInsert, RuleActionKind.LookupOrInsert, RuleActionKind.LookupOrInsert, RuleActionKind.Union, RuleActionKind.Insert, RuleActionKind.LookupOrInsert, RuleActionKind.Union],
                egraph.GetRuleInfo(rule).Query.Actions.Select(a => a.Kind));
        }

        [Fact]
        public void TheRightHandSideRejectsUnboundVariables()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            Assert.Throws<InvalidOperationException>(() => ExpressionBuilder.DefineRule(egraph,
                [Call(fx.Same, "a", "b").Is("s")],
                [RuleCommand.Union("a", "c")]));
            Assert.Throws<InvalidOperationException>(() => ExpressionBuilder.DefineRule(egraph,
                [Call(fx.Same, "a", "b").Is("s")],
                [RuleCommand.Set(Call(fx.Add, "a", "c"), "s")]));
        }

        [Fact]
        public void ConstantsAreUsedAsGiven()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var n1 = fx.Number(1);
            var one = egraph.BaseValueConstant<Int64Codec, long>(1);
            var rule = ExpressionBuilder.DefineRule(egraph,
                [Call(fx.Num, one).Is("x")],
                [RuleCommand.Set(Call(fx.Same, "x", RuleEntry.Const(n1, ColumnTy.Id)), "x")]);
            var atom = egraph.GetRuleInfo(rule).Query.Atoms.Single();
            Assert.Equal(one, atom.Entries[0]);
            Assert.True(egraph.RunRules(rule).Changed);
            Assert.Equal(n1, egraph.LookupId(fx.Same, [n1, n1]));
        }

        [Fact]
        public void AddExpressionsAddsGroundTermsInnermostFirst()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var one = Int(egraph, 1);
            var two = Int(egraph, 2);
            ExpressionBuilder.AddExpressions(egraph, Call(fx.Add, Call(fx.Num, one), Call(fx.Num, two)), Call(fx.Num, one));
            Assert.Equal(2, egraph.TableSize(fx.Num));
            Assert.Single(Rows(egraph, fx.Add));

            var n1 = egraph.LookupId(fx.Num, [one])!.Value;
            var n2 = egraph.LookupId(fx.Num, [two])!.Value;
            Assert.Equal(egraph.LookupId(fx.Add, [n1, n2]), ExpressionBuilder.AddExpression(egraph, Call(fx.Add, Call(fx.Num, one), Call(fx.Num, two))));
            Assert.Equal(one, ExpressionBuilder.AddExpression(egraph, one));
            Assert.Throws<ArgumentException>(() => ExpressionBuilder.AddExpression(egraph, Call(fx.Num, "x")));
        }

        [Fact]
        public void TheBuilderBindsNamesOnce()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var rb = egraph.NewRule("builder", true);
            var builder = new ExpressionBuilder();
            var i64 = builder.InferType(fx.Num, 0, rb);
            Assert.Equal(egraph.GetFunctionInfo(fx.Num).Schema[0], i64);
            Assert.Equal(ColumnTy.Id, builder.InferReturnType(fx.Num, rb));
            Assert.Throws<ArgumentException>(() => builder.InferType(fx.Num, 2, rb));

            var x = builder.GetVar("x", fx.Num, 0, rb);
            Assert.Equal(x, builder.GetVar("x", fx.Add, 0, rb));
            Assert.Equal(x, builder.BindOrLookupVar("x", ColumnTy.Id, rb));
            Assert.Equal(i64, rb.Query.Variables[x.Variable.Id.Index].Type);

            var y = builder.BindOrLookupVar("y", ColumnTy.Id, rb);
            builder.BindVar("y", x.Variable.Id);
            Assert.True(builder.TryLookupVar("y", out var still));
            Assert.Equal(y, still);
            builder.BindVar("z", x.Variable.Id);
            Assert.True(builder.TryLookupVar("z", out var z));
            Assert.Equal(new RuleVariable(x.Variable.Id, "z"), z.Variable);
            Assert.False(builder.TryLookupVar("w", out _));
        }

        [Fact]
        public void TermsFactsAndCommandsCheckTheirShapes()
        {
            var f = new FunctionId(0);
            Assert.Throws<ArgumentException>(() => Var("x").Is("y"));
            Assert.Throws<ArgumentException>(() => Call(f, "x").Is(RuleEntry.Const(new Value(1), ColumnTy.Id)));
            Assert.Throws<ArgumentException>(() => RuleCommand.Let("t", "x"));
            Assert.Throws<ArgumentException>(() => RuleCommand.Set("x", "y"));
            Assert.Throws<ArgumentNullException>(() => Call(f, null!));

            var term = Call(f, "x", Call(f), new Value(3));
            Assert.Equal(RuleTermKind.Call, term.Kind);
            Assert.Equal(3, term.Args.Count);
            Assert.Equal(RuleTermKind.Variable, term.Args[0].Kind);
            Assert.Equal(RuleTermKind.Constant, term.Args[2].Kind);
            Assert.Equal("(fn0 x (fn0) {3:Id})", term.ToString());
            Assert.Equal("(-> (fn0 x (fn0) {3:Id}) y)", term.Is("y").ToString());
            Assert.Equal("(union x y)", RuleCommand.Union("x", "y").ToString());
            Assert.Equal("(let t (fn0 x))", RuleCommand.Let("t", Call(f, "x")).ToString());
            Assert.Equal("(set (fn0 x) y)", RuleCommand.Set(Call(f, "x"), "y").ToString());
        }

    }

}
