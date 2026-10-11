using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial review tests for the rules module (egglog-bridge/src/rule.rs, lib.rs run_rules). Each test exposes one
    /// confirmed defect and is expected to fail until it is fixed.
    /// </summary>
    public class RulesReviewTests
    {

        /// <summary>
        /// Registers an i64 addition primitive that counts its calls.
        /// </summary>
        static ExternalFunctionId CountingAdd(EGraph egraph, Action onCall)
        {
            return Register(egraph, (state, args) =>
            {
                onCall();
                var a = state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0]));
                var b = state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1]));
                return state.BaseValues.Get<Int64Codec, long>(a + b).Rep;
            });
        }

        /// <summary>
        /// Subsuming a key the table lacks must write the default row once. The map §8.1 row 19 fix reads the
        /// subsumption cell with a default of NOT_SUBSUMED, so for a missing key the subsumed row is staged twice (once by
        /// the lookup that inserts it, once by the InsertIfEq), and the duplicate is merged with the function's merge:
        /// a non-idempotent primitive merge such as <c>(+ old new)</c> turns the default 5 into 10. egglog stages the
        /// row once (and halts the lane), so the row holds 5.
        /// </summary>
        [Fact]
        public void SubsumingAMissingKeyWritesTheDefaultRowOnce()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var calls = 0;
            var add = CountingAdd(egraph, () => calls++);
            var f = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, i64],
                Default = DefaultVal.Const(Int(egraph, 5)),
                Merge = MergeFn.Primitive(add, MergeFn.Old, MergeFn.New),
                Name = "f",
                CanSubsume = true,
            });

            var rb = egraph.NewRule("subsume f(3)", true);
            rb.Subsume(f, [egraph.BaseValueConstant<Int64Codec, long>(3)]);
            egraph.RunRules(rb.Build());

            var rows = Rows(egraph, f);
            Assert.Single(rows);
            Assert.True(rows[0].Subsumed);
            Assert.Equal(5, Unint(egraph, rows[0].Values[1]));
            Assert.Equal(0, calls);
        }

        /// <summary>
        /// When a run throws an ordinary exception, <see cref="EGraph.RunRules"/> restores the rules' watermarks "so the
        /// next run finds the matches again", but the action batches that already ran before the exception have flushed
        /// their staged writes into the tables' pending buffers, which the next merge applies. The matches of those
        /// batches are then applied twice: with a <c>(+ old new)</c> counter, 200 matches count 328.
        /// </summary>
        [Fact]
        public void AnExceptionDoesNotApplyTheMatchesBeforeItTwice()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var add = CountingAdd(egraph, () => { });
            var src = Constructor(egraph, "src", false, i64);
            var acc = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, i64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(add, MergeFn.Old, MergeFn.New),
                Name = "acc",
            });

            const int N = 200;
            for (var i = 0; i < N; i++)
                egraph.AddTerm(src, [Int(egraph, i)]);

            var fail = true;
            var gateCalls = 0;
            var gate = Register(egraph, (_, args) =>
            {
                if (fail && ++gateCalls > 150)
                    throw new InvalidOperationException("gate closed");

                return args[0];
            });

            var rb = egraph.NewRule("count", true);
            var k = rb.NewVar(i64);
            var id = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(src, [k, id], false);
            rb.CallExternalFunc(gate, [k], i64, () => "gate");
            rb.Set(acc, [egraph.BaseValueConstant<Int64Codec, long>(0), egraph.BaseValueConstant<Int64Codec, long>(1)]);
            var rule = rb.Build();

            Assert.Throws<InvalidOperationException>(() => egraph.RunRules(rule));
            Assert.Equal(0u, egraph.GetRuleInfo(rule).LastRunAt);

            fail = false;
            var report = egraph.RunRules(rule);
            Assert.Equal(N, report.RuleSetReport.NumMatches("count"));
            var rows = Rows(egraph, acc);
            Assert.Single(rows);
            Assert.Equal(N, Unint(egraph, rows[0].Values[1]));
        }

        /// <summary>
        /// <see cref="EGraphRuleBuilder"/> documents that "every method checks its input before recording anything, so a
        /// failing call leaves the rule unchanged", but <c>RebuildRow</c> records the <c>Remove</c> of the old key before
        /// <c>SetWithSubsume</c> checks the new row's entries; a bad entry in <c>after</c> throws and leaves the removal
        /// recorded.
        /// </summary>
        [Fact]
        public void AFailingRebuildRowRecordsNothing()
        {
            var egraph = NewEGraph();
            var f = Constructor(egraph, "f", false, ColumnTy.Id);
            var rb = egraph.NewRule("rebuild", true);
            var v0 = rb.NewVar(ColumnTy.Id);
            var v1 = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(f, [v0, v1], false);
            var foreign = new RuleVariable(new VariableId(40), null);

            Assert.Throws<ArgumentException>(() => rb.RebuildRow(f, [v0, v1], [foreign, v1], null));
            Assert.Empty(rb.Query.Actions);
        }

    }

}
