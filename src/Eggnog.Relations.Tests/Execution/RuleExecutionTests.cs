using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Ports of egglog's rule-execution tests (core-relations/src/tests.rs), run through
    /// <see cref="Database.RunRuleSet"/> in every executor mode (egglog's <c>run_serial_and_parallel</c>; see
    /// <see cref="ExecutionTestUtil"/>).
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class RuleExecutionTests
    {

        public static IEnumerable<object[]> ModesData => ExecutionTestUtil.Modes.Select(m => new object[] { m });

        public static IEnumerable<object[]> ModesAndStrategies =>
            from m in ExecutionTestUtil.Modes
            from s in new[] { "Gj", "PureSize", "MinCover" }
            select new object[] { m, s };

        /// <summary>
        /// egglog's <c>basic_query</c> (tests.rs:40-138): <c>Add(x, y, z), Num(a, x), Num(b, y) =&gt; Num(a + b, z)</c>.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void BasicQuery(string mode)
        {
            var m = new ExecutionMathEgraph(mode);
            var db = m.Db;
            var bases = new BaseValues();
            bases.RegisterType<Int64Codec, long>();
            var addInt = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                var x = bases.Unwrap<Int64Codec, long>(new Value(args[0]));
                var y = bases.Unwrap<Int64Codec, long>(new Value(args[1]));
                result = bases.Get<Int64Codec, long>(x + y).Rep;
                return true;
            }));

            var ids = new List<uint>();
            var numBuf = db.NewBuffer(m.Num);
            for (var i = 0; i < 10; i++)
            {
                var id = (uint)db.IncrementCounter(m.IdCounter);
                var v = bases.Get<Int64Codec, long>(i).Rep;
                ids.Add(v);
                numBuf.StageInsert([v, id, 0]);
            }

            numBuf.Flush();
            db.MergeAll();
            var addBuf = db.NewBuffer(m.Add);
            for (var i = 0; i < ids.Count; i += 2)
                addBuf.StageInsert([ids[i], ids[i + 1], (uint)db.IncrementCounter(m.IdCounter), 0]);

            addBuf.Flush();
            db.MergeAll();

            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            var t1 = q.NewVariable("t1");
            var t2 = q.NewVariable("t2");
            var t3 = q.NewVariable("t3");
            var a = q.NewVariable("a");
            var b = q.NewVariable("b");
            q.AddAtom(m.Add, [x, y, z, t1]);
            q.AddAtom(m.Num, [a, x, t2]);
            q.AddAtom(m.Num, [b, y, t3]);
            var rb = q.Build();
            var sum = rb.CallExternal(addInt, [a, b]);
            rb.Insert(m.Num, [sum, z, V(1)]);
            rb.Build("add");
            using var rs = rsb.Build();

            var report = db.RunRuleSet(rs);
            Assert.True(report.Changed, report.ToString());
            Assert.Equal(5, report.NumMatches("add"));
            var values = DatabaseTestUtil.Rows(db, m.Num).Select(r => bases.Unwrap<Int64Codec, long>(new Value(r[0]))).OrderBy(v => v).ToArray();
            Assert.Equal(Enumerable.Range(0, 10).Select(i => (long)i).Concat([13L, 17L]), values);
        }

        /// <summary>
        /// egglog's <c>line_graph_1_test</c> (tests.rs:140-210): <c>edge(x, y), edge(y, z) =&gt; edge(x, z)</c>.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesAndStrategies))]
        public void LineGraph1(string mode, string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var (db, edges, nodes) = LineGraph(mode);
            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            q.Strategy = strategy;
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            q.AddAtom(edges, [x, y]);
            q.AddAtom(edges, [y, z]);
            var rb = q.Build();
            rb.Insert(edges, [x, z]);
            rb.Build();
            using var rs = rsb.Build();

            Assert.True(db.RunRuleSet(rs).Changed);
            var expected = ExecutionTestUtil.Windows(nodes, 2).Select(w => new[] { w[0], w[1] }).Concat(ExecutionTestUtil.Windows(nodes, 3).Select(w => new[] { w[0], w[2] }));
            Assert.Equal(Show(expected), Rows(db, edges));
        }

        /// <summary>
        /// egglog's <c>line_graph_2_test</c> (tests.rs:212-293): the same with <c>y &gt; 1</c> on the first atom.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesAndStrategies))]
        public void LineGraph2(string mode, string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var (db, edges, nodes) = LineGraph(mode);
            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            q.Strategy = strategy;
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            q.AddAtom(edges, [x, y], [Constraint.GtConst(C(1), V(1))]);
            q.AddAtom(edges, [y, z]);
            var rb = q.Build();
            rb.Insert(edges, [x, z]);
            rb.Build();
            using var rs = rsb.Build();

            Assert.True(db.RunRuleSet(rs).Changed);
            var expected = ExecutionTestUtil.Windows(nodes, 2).Select(w => new[] { w[0], w[1] }).Concat(ExecutionTestUtil.Windows(nodes, 3).Where(w => w[1] > 1).Select(w => new[] { w[0], w[2] }));
            Assert.Equal(Show(expected), Rows(db, edges));
        }

        /// <summary>
        /// egglog's <c>intersection_test</c> (tests.rs:295-385): <c>R(x, x), S(x, x), T(x, x), x &gt; 5 =&gt; U(x)</c>.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesAndStrategies))]
        public void Intersection(string mode, string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var db = ExecutionTestUtil.NewDatabase(mode);
            var rst = Enumerable.Range(0, 3).Select(_ => db.AddTable(Strict(2, 2))).ToArray();
            var u = db.AddTable(Strict(1, 1));
            foreach (var rel in rst)
                DatabaseTestUtil.Stage(db, rel, Enumerable.Range(0, 10).Select(i => new[] { (uint)i, (uint)i }).ToArray());

            db.MergeAll();
            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            q.Strategy = strategy;
            var x = q.NewVariable("x");
            foreach (var rel in rst)
                q.AddAtom(rel, [x, x], [Constraint.GtConst(C(0), V(5))]);

            var rb = q.Build();
            rb.Insert(u, [x]);
            rb.Build();
            using var rs = rsb.Build();

            Assert.True(db.RunRuleSet(rs).Changed);
            Assert.Equal(["[6]", "[7]", "[8]", "[9]"], Rows(db, u));
        }

        /// <summary>
        /// egglog's <c>minimal_ac</c> (tests.rs:387-496): one associativity step restricted by timestamp ranges.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void MinimalAc(string mode)
        {
            var m = new ExecutionMathEgraph(mode);
            var db = m.Db;
            DatabaseTestUtil.Stage(db, m.Add, [0, 0, 1, 0], [0, 1, 2, 0], [0, 2, 3, 0]);
            db.MergeAll();
            DatabaseTestUtil.Stage(db, m.Add, [1, 0, 2, 1], [1, 1, 3, 1]);
            db.MergeAll();

            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            var i1 = q.NewVariable("i1");
            var i2 = q.NewVariable("i2");
            var t1 = q.NewVariable("t1");
            var t2 = q.NewVariable("t2");
            q.AddAtom(m.Add, [y, z, i1, t1], [Constraint.GeConst(C(3), V(0)), Constraint.LtConst(C(3), V(1))]);
            q.AddAtom(m.Add, [x, i1, i2, t2], [Constraint.GeConst(C(3), V(1)), Constraint.LtConst(C(3), V(2))]);
            var rb = q.Build();
            var res = rb.LookupOrInsert(m.Add, [x, y], [WriteVal.FromCounter(m.IdCounter), V(2)], C(2));
            rb.Insert(m.Add, [res, z, i2, V(2)]);
            rb.Build();
            using var rs = rsb.Build();

            db.RunRuleSet(rs);
            Assert.Equal(["[0, 0, 1, 0]", "[0, 1, 2, 0]", "[0, 2, 3, 0]", "[1, 0, 2, 1]", "[1, 1, 3, 1]", "[2, 0, 3, 2]"], Rows(db, m.Add));
        }

        /// <summary>
        /// egglog's <c>ac_test_inner</c> (tests.rs:498-951): associativity and commutativity to saturation with
        /// semi-naive timestamp ranges, rule-based rebuilding chosen by size estimates, and iteration until nothing
        /// changes; the left- and right-associated sums of 0..4 must end up equal.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesAndStrategies))]
        public void AssociativityAndCommutativitySaturate(string mode, string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var m = new ExecutionMathEgraph(mode);
            var (leftRoot, rightRoot) = AcSaturation.Run(m.Db, m.Num, m.Add, m.Uf, m.IdCounter, strategy, 5);
            Assert.Equal(m.Find(leftRoot), m.Find(rightRoot));
        }

        /// <summary>
        /// egglog's <c>lookup_with_fallback_partial_success</c> (tests.rs:1016-1114).
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void LookupWithFallbackPartialSuccess(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var f = db.AddTable(Strict(1, 2));
            var g = db.AddTable(Strict(1, 2));
            var h = db.AddTable(Strict(1, 2));
            DatabaseTestUtil.Stage(db, f, [1, 0], [2, 0]);
            DatabaseTestUtil.Stage(db, g, [1, 0], [3, 0], [4, 0], [5, 0]);
            db.MergeAll();
            var log = new List<uint[]>();
            var logVals = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var assertEven = db.AddExternalFunction(Function(args => args[0] % 2 == 0 ? args[0] : null));

            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(g, [x, y]);
            var rb = q.Build();
            var res = rb.LookupWithFallback(f, [x], C(0), assertEven, [x]);
            rb.CallExternal(logVals, [x]);
            rb.Insert(h, [res, y]);
            rb.Build();
            using var rs = rsb.Build();

            Assert.True(db.RunRuleSet(rs).Changed);
            Assert.Equal(["[1, 0]", "[4, 0]"], Rows(db, h));
            Assert.Equal([1u, 4u], log.Select(r => r[0]).OrderBy(v => v));
        }

        /// <summary>
        /// egglog's <c>call_external_with_fallback</c> (tests.rs:1116-1200).
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void CallExternalWithFallback(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var f = db.AddTable(Strict(1, 2));
            var h = db.AddTable(Strict(1, 2));
            DatabaseTestUtil.Stage(db, f, [1, 0], [2, 0], [3, 0], [5, 0]);
            db.MergeAll();
            var assertEven = db.AddExternalFunction(Function(args => args[0] % 2 == 0 ? args[0] : null));
            var inc = db.AddExternalFunction(Function(args => args[0] == 5 ? null : args[0] + 1));

            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(f, [x, y]);
            var rb = q.Build();
            var res = rb.CallExternalWithFallback(assertEven, [x], inc, [x]);
            rb.Insert(h, [res, y]);
            rb.Build();
            using var rs = rsb.Build();

            Assert.True(db.RunRuleSet(rs).Changed);
            Assert.Equal(["[2, 0]", "[4, 0]"], Rows(db, h));
        }

        /// <summary>
        /// egglog's <c>early_stop</c> (tests.rs:1202-1284): a scan of 500,000 rows whose action triggers the early stop
        /// after 1000 calls must stop long before the end.
        /// </summary>
        [Theory]
        [InlineData("Serial")]
        [InlineData("Parallel4")]
        [InlineData("Default")]
        public void EarlyStop(string mode)
        {
            // "Default" lets the database pick: half a million rows go parallel on a multi-core machine, as in egglog.
            var db = mode == "Default" ? new Database() : ExecutionTestUtil.NewDatabase(mode);
            var data = db.AddTable(new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, new ClosureMerge((_, _) => null), ParallelHeuristics.Serial));
            var buffer = db.NewBuffer(data);
            for (uint i = 0; i < 500_000; i++)
                buffer.StageInsert([i, i]);

            buffer.Flush();
            db.MergeAll();

            var calls = 0;
            var stopTrigger = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                if (System.Threading.Interlocked.Increment(ref calls) >= 1000)
                    state.TriggerEarlyStop();

                result = args[0];
                return true;
            }));

            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(data, [x, y]);
            var rb = q.Build();
            rb.CallExternal(stopTrigger, [x]);
            rb.Build("early_stop_test");
            using var rs = rsb.Build();

            var report = db.RunRuleSet(rs);
            var matches = report.NumMatches("early_stop_test");
            Assert.True(matches < 100_000, $"expected far fewer matches after the early stop, got {matches} (calls {calls})");
            Assert.True(matches >= 1000, $"expected at least 1000 matches before stopping, got {matches} (calls {calls})");
            Assert.True(calls >= 1000, $"the external function ran {calls} times");
            Assert.True(calls < 100_000, $"the external function ran {calls} times");
        }

        static (Database Db, TableId Edges, uint[] Nodes) LineGraph(string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var edges = db.AddTable(Strict(2, 2));
            var nodes = Enumerable.Range(0, 10).Select(i => (uint)i).ToArray();
            DatabaseTestUtil.Stage(db, edges, ExecutionTestUtil.Windows(nodes, 2).ToArray());
            db.MergeAll();
            return (db, edges, nodes);
        }

    }

}
