using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Ports of egglog's rule tests (core-relations/src/tests.rs): each builds its rules exactly as egglog's does, checks
    /// what the builders produced, and runs the rule set with <see cref="NaiveRuleRunner"/> in place of
    /// <c>run_rule_set</c> to check egglog's expected table contents.
    /// </summary>
    public class RuleSetBuilderTests
    {

        /// <summary>
        /// egglog's <c>basic_query</c> (tests.rs:40-138).
        /// </summary>
        [Fact]
        public void BasicQuery()
        {
            var m = new MathEgraph();
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

            // Add the numbers 0 through 9 to the num table at timestamp 0.
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

            // Add(x, y, z, t1), Num(a, x, t2), Num(b, y, t3) => Num(+ a b, z, 1)
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
            var rule = rb.Build("add");
            using var rs = rsb.Build();

            Assert.Equal(new RuleId(0), rule);
            var plan = rs[rule];
            Assert.Equal("add", plan.Description);
            Assert.Equal(new ActionId(0), plan.Action);
            var action = rs.GetAction(plan.Action);
            Assert.Equal([z, a, b], action.UsedVariables);
            Assert.Equal(["v8 <- External f0 [v6, v7]", "Insert t1 [v8, v2, 1]"], Instrs(action));
            Assert.True(plan.Symbols.TryGetAtomName(new AtomId(1), out var atomName));
            Assert.Equal("num", atomName);
            Assert.True(plan.Symbols.TryGetVariableName(t3, out var varName));
            Assert.Equal("t3", varName);
            Assert.False(plan.Symbols.TryGetVariableName(sum, out _));

            var (changed, matches) = NaiveRuleRunner.Run(db, rs);
            Assert.True(changed);
            Assert.Equal(5, matches[0]);
            var values = DatabaseTestUtil.Rows(db, m.Num).Select(r => bases.Unwrap<Int64Codec, long>(new Value(r[0]))).OrderBy(v => v).ToArray();
            Assert.Equal(Enumerable.Range(0, 10).Select(i => (long)i).Concat([13L, 17L]), values);
        }

        /// <summary>
        /// egglog's <c>line_graph_1_test</c> (tests.rs:155-210): <c>edge(x, y), edge(y, z) =&gt; edge(x, z)</c>.
        /// </summary>
        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void LineGraph1(string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var (db, edges, nodes) = LineGraph();
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

            Assert.Equal(PlanKind.Single, rs[new RuleId(0)].Template.Kind);
            Assert.True(NaiveRuleRunner.Run(db, rs).Changed);
            var expected = Windows(nodes, 2).Select(w => new[] { w[0], w[1] }).Concat(Windows(nodes, 3).Select(w => new[] { w[0], w[2] }));
            Assert.Equal(Show(expected), Rows(db, edges));
        }

        /// <summary>
        /// egglog's <c>line_graph_2_test</c> (tests.rs:227-293): the same with <c>y &gt; 1</c> on the first atom.
        /// </summary>
        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void LineGraph2(string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var (db, edges, nodes) = LineGraph();
            var rsb = new RuleSetBuilder(db);
            var q = rsb.NewRule();
            q.Strategy = strategy;
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            var first = q.AddAtom(edges, [x, y], [Constraint.GtConst(C(1), V(1))]);
            q.AddAtom(edges, [y, z]);
            var rb = q.Build();
            rb.Insert(edges, [x, z]);
            rb.Build();
            using var rs = rsb.Build();

            // The table has no sort column and the comparison is not an equality, so it stays slow.
            Assert.Equal(["c1 > 1"], rs[new RuleId(0)].Template.Atoms[first].SlowConstraints.Select(c => c.ToString()));
            Assert.Empty(rs[new RuleId(0)].Template.Headers);
            Assert.True(NaiveRuleRunner.Run(db, rs).Changed);
            var expected = Windows(nodes, 2).Select(w => new[] { w[0], w[1] }).Concat(Windows(nodes, 3).Where(w => w[1] > 1).Select(w => new[] { w[0], w[2] }));
            Assert.Equal(Show(expected), Rows(db, edges));
        }

        /// <summary>
        /// egglog's <c>intersection_test</c> (tests.rs:295-370): <c>R(x, x), S(x, x), T(x, x), x &gt; 5 =&gt; U(x)</c>.
        /// </summary>
        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void Intersection(string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var db = new Database(ParallelHeuristics.Serial);
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

            // The repeated variable adds c1 == c0 after the given constraint, both slow.
            var atom = rs[new RuleId(0)].Template.Atoms[new AtomId(2)];
            Assert.Equal(["c0 > 5", "c1 == c0"], atom.SlowConstraints.Select(c => c.ToString()));
            Assert.True(NaiveRuleRunner.Run(db, rs).Changed);
            Assert.Equal(["[6]", "[7]", "[8]", "[9]"], Rows(db, u));
        }

        /// <summary>
        /// egglog's <c>minimal_ac</c> (tests.rs:387-496): one associativity step restricted by timestamp ranges.
        /// </summary>
        [Fact]
        public void MinimalAc()
        {
            var m = new MathEgraph();
            var db = m.Db;
            DatabaseTestUtil.Stage(db, m.Add, [0, 0, 1, 0], [0, 1, 2, 0], [0, 2, 3, 0]);
            db.MergeAll();
            DatabaseTestUtil.Stage(db, m.Add, [1, 0, 2, 1], [1, 1, 3, 1]);
            db.MergeAll();

            // Add(y, z, i1, t1), Add(x, i1, i2, t2) => Add(x, y, <res>, 2), Add(<res>, z, i2, 2)
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

            // The timestamp ranges are on the sort column: fast, so they become headers with their subsets.
            var plan = rs[new RuleId(0)];
            Assert.NotNull(plan.Instance);
            Assert.Equal(["a0{c3 >= 0, c3 < 1}", "a1{c3 >= 1, c3 < 2}"], plan.Instance.Headers.Select(h => h.ToString()));
            Assert.Equal([3, 2], plan.Instance.HeaderSubsets.Select(s => s.Size));
            Assert.Equal([x, y, z, i2], rs.GetAction(plan.Action).UsedVariables);

            NaiveRuleRunner.Run(db, rs);
            Assert.Equal(["[0, 0, 1, 0]", "[0, 1, 2, 0]", "[0, 2, 3, 0]", "[1, 0, 2, 1]", "[1, 1, 3, 1]", "[2, 0, 3, 2]"], Rows(db, m.Add));
        }

        /// <summary>
        /// egglog's <c>ac_test_inner</c> (tests.rs:513-951): associativity and commutativity to saturation, with
        /// semi-naive timestamp ranges, rule-based rebuilding chosen by size estimates, and iteration until nothing
        /// changes; the left- and right-associated sums of 0..4 must end up equal.
        /// </summary>
        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        [InlineData("MinCover")]
        public void AssociativityAndCommutativitySaturate(string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            const int N = 5;
            var m = new MathEgraph();
            var db = m.Db;
            var (num, add, uf, idCounter) = (m.Num, m.Add, m.Uf, m.IdCounter);
            var bases = new BaseValues();
            bases.RegisterType<Int64Codec, long>();

            var ids = new List<uint>();
            for (var i = 0; i < N; i++)
            {
                var id = (uint)db.IncrementCounter(idCounter);
                var v = bases.Get<Int64Codec, long>(i).Rep;
                ids.Add(v);
                DatabaseTestUtil.Stage(db, num, DatabaseTestUtil.R(v, id, 0));
            }

            db.MergeAll();

            // (0 + ... + N) left-associated and (N + ... + 0) right-associated.
            uint leftRoot = 0, rightRoot = 0;
            var prev = ids[0];
            foreach (var n in ids.Skip(1))
            {
                var id = (uint)db.IncrementCounter(idCounter);
                DatabaseTestUtil.Stage(db, add, DatabaseTestUtil.R(n, prev, id, 0));
                prev = id;
                leftRoot = id;
            }

            prev = ids[N - 1];
            for (var i = N - 2; i >= 0; i--)
            {
                var id = (uint)db.IncrementCounter(idCounter);
                DatabaseTestUtil.Stage(db, add, DatabaseTestUtil.R(prev, ids[i], id, 0));
                prev = id;
                rightRoot = id;
            }

            db.MergeAll();

            bool RunAcRule(uint recentStart, uint recentEnd)
            {
                var nextTs = recentEnd;
                var rsb = new RuleSetBuilder(db);
                foreach (var (lStart, lEnd, rStart, rEnd) in new[] { (0u, recentEnd, recentStart, recentEnd), (recentStart, recentEnd, 0u, recentStart) })
                {
                    // Add(x, Add(y, z)) => Add(Add(x, y), z)
                    var assoc = rsb.NewRule();
                    assoc.Strategy = strategy;
                    var x = assoc.NewVariable("x");
                    var y = assoc.NewVariable("y");
                    var z = assoc.NewVariable("z");
                    var i1 = assoc.NewVariable("i1");
                    var i2 = assoc.NewVariable("i2");
                    var t1 = assoc.NewVariable("t1");
                    var t2 = assoc.NewVariable("t2");
                    assoc.AddAtom(add, [y, z, i1, t1], [Constraint.GeConst(C(3), V(lStart)), Constraint.LtConst(C(3), V(lEnd))]);
                    assoc.AddAtom(add, [x, i1, i2, t2], [Constraint.GeConst(C(3), V(rStart)), Constraint.LtConst(C(3), V(rEnd))]);
                    var rules = assoc.Build();
                    var res = rules.LookupOrInsert(add, [x, y], [WriteVal.FromCounter(idCounter), V(nextTs)], C(2));
                    rules.Insert(add, [res, z, i2, V(nextTs)]);
                    rules.Build();
                }

                // Add(x, y, z, t1) => Add(y, x, z, cur), for the current timestamp only.
                var comm = rsb.NewRule();
                comm.Strategy = strategy;
                var cx = comm.NewVariable("x");
                var cy = comm.NewVariable("y");
                var cz = comm.NewVariable("z");
                var ct = comm.NewVariable("t1");
                comm.AddAtom(add, [cx, cy, cz, ct], [Constraint.EqConst(C(3), V(recentStart))]);
                var commRules = comm.Build();
                commRules.Insert(add, [cy, cx, cz, V(nextTs)]);
                commRules.Build();
                using var rs = rsb.Build();
                return NaiveRuleRunner.Run(db, rs).Changed;
            }

            void NumRebuild(RuleSetBuilder rsb, uint curTs, uint nextTs)
            {
                var numSize = rsb.EstimateSize(num);
                var ufSize = rsb.EstimateSize(uf, Constraint.EqConst(C(2), V(curTs)));
                var q = rsb.NewRule();
                q.Strategy = strategy;
                if (IncrementalRebuild(ufSize, numSize))
                {
                    // Nonincremental: num(x, id, t1) => num(x, id', next) where id' is canonical.
                    var x = q.NewVariable("x");
                    var id = q.NewVariable("id");
                    var t1 = q.NewVariable("t1");
                    q.AddAtom(num, [x, id, t1]);
                    var rules = q.Build();
                    var canon = rules.LookupWithDefault(uf, [id], id, C(1));
                    rules.AssertNe(id, canon);
                    rules.Insert(num, [x, canon, V(nextTs)]);
                    rules.Build();
                }
                else
                {
                    var x = q.NewVariable("x");
                    var id = q.NewVariable("id");
                    var t1 = q.NewVariable("t1");
                    var idNew = q.NewVariable("id_new");
                    var t2 = q.NewVariable("t2");
                    q.AddAtom(num, [x, id, t1]);
                    q.AddAtom(uf, [id, idNew, t2], [Constraint.EqConst(C(2), V(curTs))]);
                    var rules = q.Build();
                    rules.Insert(num, [x, idNew, V(nextTs)]);
                    rules.Build();
                }
            }

            // The add rebuild rule that joins column `joined` (0, 1 or 2) with the union-find's newest rows.
            void AddRebuild(RuleSetBuilder rsb, uint curTs, uint nextTs, int joined)
            {
                var q = rsb.NewRule();
                q.Strategy = strategy;
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var id = q.NewVariable("id");
                var t1 = q.NewVariable("t1");
                var fresh = q.NewVariable(joined switch { 0 => "x_new", 1 => "y_new", _ => "id_new" });
                var t2 = q.NewVariable("t2");
                q.AddAtom(add, [x, y, id, t1]);
                q.AddAtom(uf, [joined switch { 0 => x, 1 => y, _ => id }, fresh, t2], [Constraint.EqConst(C(2), V(curTs))]);
                var rules = q.Build();
                var xNew = joined == 0 ? fresh : rules.LookupWithDefault(uf, [x], x, C(1));
                var yNew = joined == 1 ? fresh : rules.LookupWithDefault(uf, [y], y, C(1));
                var idNew = joined == 2 ? fresh : rules.LookupWithDefault(uf, [id], id, C(1));
                rules.Remove(add, [x, y]);
                rules.Insert(add, [xNew, yNew, idNew, V(nextTs)]);
                rules.Build();
            }

            (uint Next, bool Changed) Rebuild(uint curTs)
            {
                var nextTs = curTs + 1;
                var rsb = db.NewRuleSet();
                NumRebuild(rsb, curTs, nextTs);
                var changed = false;
                var addSize = rsb.EstimateSize(add);
                var ufSize = rsb.EstimateSize(uf, Constraint.EqConst(C(2), V(curTs)));
                if (IncrementalRebuild(ufSize, addSize))
                {
                    // egglog's order: the id column first (with num), then the left, then the right operand.
                    foreach (var joined in new[] { 2, 0, 1 })
                    {
                        if (joined != 2)
                        {
                            rsb = db.NewRuleSet();
                            NumRebuild(rsb, curTs, nextTs);
                        }

                        AddRebuild(rsb, curTs, nextTs, joined);
                        using var rs = rsb.Build();
                        changed |= NaiveRuleRunner.Run(db, rs).Changed;
                    }
                }
                else
                {
                    // Nonincremental: recanonicalize every add row that changes.
                    var q = rsb.NewRule();
                    q.Strategy = strategy;
                    var x = q.NewVariable("x");
                    var y = q.NewVariable("y");
                    var id = q.NewVariable("id");
                    var t1 = q.NewVariable("t1");
                    q.AddAtom(add, [x, y, id, t1]);
                    var rules = q.Build();
                    var xc = rules.LookupWithDefault(uf, [x], x, C(1));
                    var yc = rules.LookupWithDefault(uf, [y], y, C(1));
                    var idc = rules.LookupWithDefault(uf, [id], id, C(1));
                    rules.AssertAnyNe([x, y, id], [xc, yc, idc]);
                    rules.Remove(add, [x, y]);
                    rules.Insert(add, [xc, yc, idc, V(nextTs)]);
                    rules.Build();
                    using var rs = rsb.Build();
                    changed |= NaiveRuleRunner.Run(db, rs).Changed;
                }

                return (nextTs, changed);
            }

            uint cur = 0, next = 1;
            var iterations = 0;
            while (RunAcRule(cur, next))
            {
                Assert.True(++iterations < 100, "saturation should take few iterations");
                var start = next;
                var newIdsAt = start;
                var changed = true;
                while (changed)
                    (newIdsAt, changed) = Rebuild(newIdsAt);

                cur = start;
                next = newIdsAt + 1;
            }

            Assert.Equal(m.Find(leftRoot), m.Find(rightRoot));
        }

        /// <summary>
        /// egglog's <c>lookup_with_fallback_partial_success</c> (tests.rs:1016-1114).
        /// </summary>
        [Fact]
        public void LookupWithFallbackPartialSuccess()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Strict(1, 2));
            var g = db.AddTable(Strict(1, 2));
            var h = db.AddTable(Strict(1, 2));
            DatabaseTestUtil.Stage(db, f, [1, 0], [2, 0]);
            DatabaseTestUtil.Stage(db, g, [1, 0], [3, 0], [4, 0], [5, 0]);
            db.MergeAll();
            var log = new List<uint>();
            var logVals = db.AddExternalFunction(Function(args =>
            {
                lock (log)
                    log.Add(args[0]);

                return args[0];
            }));
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

            Assert.Equal(["v2 <- LookupWithFallback t0 [v0] c0 ?? f1 [v0]", "v3 <- External f0 [v0]", "Insert t2 [v2, v1]"], Instrs(rs.GetAction(new ActionId(0))));
            Assert.True(NaiveRuleRunner.Run(db, rs).Changed);
            Assert.Equal(["[1, 0]", "[4, 0]"], Rows(db, h));
            log.Sort();
            Assert.Equal([1u, 4u], log);
        }

        /// <summary>
        /// egglog's <c>call_external_with_fallback</c> (tests.rs:1116-1200).
        /// </summary>
        [Fact]
        public void CallExternalWithFallback()
        {
            var db = new Database(ParallelHeuristics.Serial);
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

            Assert.True(NaiveRuleRunner.Run(db, rs).Changed);
            Assert.Equal(["[2, 0]", "[4, 0]"], Rows(db, h));
        }

        /// <summary>
        /// The building half of egglog's <c>early_stop</c> (tests.rs:1202-1284); stopping is the executor's business.
        /// </summary>
        [Fact]
        public void EarlyStopRuleShape()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var data = db.AddTable(new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            DatabaseTestUtil.Stage(db, data, Enumerable.Range(0, 1000).Select(i => new[] { (uint)i, (uint)i }).ToArray());
            db.MergeAll();
            var calls = 0;
            var stopTrigger = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                if (++calls >= 100)
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
            var rule = rb.Build("early_stop_test");
            using var rs = rsb.Build();

            var plan = rs[rule];
            Assert.Equal("early_stop_test", plan.Description);
            Assert.Equal([x], rs.GetAction(plan.Action).UsedVariables);
            var single = Assert.IsType<SinglePlan>(plan.Template);
            Assert.Single(single.Stages);
            Assert.Equal(1000, NaiveRuleRunner.Run(db, rs).Matches[0]);
            Assert.Equal(1000, calls);
        }

        static bool IncrementalRebuild(int ufSize, int tableSize) => ufSize / 4 > tableSize;

        static (Database Db, TableId Edges, uint[] Nodes) LineGraph()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var edges = db.AddTable(Strict(2, 2));
            var nodes = Enumerable.Range(0, 10).Select(i => (uint)i).ToArray();
            DatabaseTestUtil.Stage(db, edges, Windows(nodes, 2).ToArray());
            db.MergeAll();
            return (db, edges, nodes);
        }

        static IEnumerable<uint[]> Windows(uint[] items, int size)
        {
            for (var i = 0; i + size <= items.Length; i++)
                yield return items[i..(i + size)];
        }

    }

}
