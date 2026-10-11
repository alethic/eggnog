using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="Database.ExecuteRuleSet"/> is all or nothing: when an action or external function throws, the writes
    /// the run staged are discarded, those of the batches that finished before the exception included, while writes
    /// staged before the run stay. Run in every executor mode.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class RuleSetRollbackTests
    {

        public static IEnumerable<object[]> ModesData => ExecutionTestUtil.Modes.Select(m => new object[] { m });

        const int Matches = 600;

        [Theory]
        [MemberData(nameof(ModesData))]
        public void AnExceptionDiscardsEveryWriteTheRunStaged(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var src = db.AddTable(Strict(1, 1));
            var acc = db.AddTable(new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, new SumMerge(1), ParallelHeuristics.Serial));
            DatabaseTestUtil.Stage(db, src, Enumerable.Range(0, Matches).Select(i => new[] { (uint)i }).ToArray());
            db.MergeAll();

            // Staged before the run and not merged: must survive the failed run.
            DatabaseTestUtil.Stage(db, acc, [7, 1000]);

            var failing = true;
            var calls = 0;
            var gate = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                if (Volatile.Read(ref failing) && Interlocked.Increment(ref calls) > Matches - 10)
                    throw new InvalidOperationException("gate closed");

                result = args[0];
                return true;
            }));

            Assert.Throws<InvalidOperationException>(() =>
            {
                using var failed = CountingRuleSet(db, src, acc, gate);
                db.RunRuleSet(failed);
            });

            db.MergeAll();
            Assert.Equal(["[7, 1000]"], Rows(db, acc));

            Volatile.Write(ref failing, false);
            using var rs = CountingRuleSet(db, src, acc, gate);
            var report = db.RunRuleSet(rs);
            Assert.Equal(Matches, report.NumMatches("count"));
            Assert.Equal([$"[0, {Matches}]", "[7, 1000]"], Rows(db, acc));
        }

        [Fact]
        public void ExecutingAndMergingInTwoStepsIsRunningTheRuleSet()
        {
            var db = ExecutionTestUtil.NewDatabase("Serial");
            var src = db.AddTable(Strict(1, 1));
            var acc = db.AddTable(new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, new SumMerge(1), ParallelHeuristics.Serial));
            DatabaseTestUtil.Stage(db, src, [1], [2], [3]);
            db.MergeAll();
            var identity = db.AddExternalFunction(Function(args => args[0]));

            using var rs = CountingRuleSet(db, src, acc, identity);
            var executed = db.ExecuteRuleSet(rs);
            Assert.False(executed.Changed);
            Assert.Equal(TimeSpan.Zero, executed.MergeTime);
            Assert.Equal(3, executed.NumMatches("count"));
            Assert.Empty(Rows(db, acc));

            var merged = db.MergeRuleSet(rs, executed);
            Assert.True(merged.Changed);
            Assert.Equal(3, merged.NumMatches("count"));
            Assert.Equal(executed.SearchAndApplyTime, merged.SearchAndApplyTime);
            Assert.Equal(["[0, 3]"], Rows(db, acc));

            // An empty rule set neither runs nor merges, as in egglog.
            DatabaseTestUtil.Stage(db, acc, [0, 1]);
            using var empty = new RuleSetBuilder(db).Build();
            Assert.Same(RuleSetReport.Empty, db.RunRuleSet(empty));
            Assert.Equal(["[0, 3]"], Rows(db, acc));
        }

        /// <summary>
        /// Builds <c>src(x) =&gt; gate(x); acc[0] += 1</c>, described "count".
        /// </summary>
        static RuleSet CountingRuleSet(Database db, TableId src, TableId acc, ExternalFunctionId gate)
        {
            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            q.AddAtom(src, [x]);
            var rb = q.Build();
            rb.CallExternal(gate, [x]);
            rb.Insert(acc, [V(0), V(1)]);
            rb.Build("count");
            return rsb.Build();
        }

    }

}
