using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// egglog's <c>ac_test_inner</c> (core-relations/src/tests.rs:513-951) on <see cref="Database.RunRuleSet"/>: it
    /// reimplements egglog on top of the engine (rule-based rebuilding with incremental and nonincremental variants
    /// chosen by size estimates, semi-naive evaluation with timestamp ranges on the sorted column, and iteration until
    /// nothing changes) on the associativity and commutativity workload.
    /// </summary>
    static class AcSaturation
    {

        /// <summary>
        /// Builds <c>(0 + ... + n-1)</c> left-associated and <c>(n-1 + ... + 0)</c> right-associated and saturates
        /// associativity and commutativity, rebuilding after every iteration.
        /// </summary>
        /// <remarks>
        /// egglog's commutativity rule matches only rows stamped exactly with the start of the recent range, so it
        /// misses rows the rebuild re-stamped later in the range; the two roots still meet, but the e-graph need not be
        /// saturated, and how much it misses depends on how many unions the run's fresh ids caused.
        /// <paramref name="wholeRecentRange"/> makes it match the whole recent range, which saturates.
        /// </remarks>
        /// <param name="db">The database of an <see cref="ExecutionMathEgraph"/>.</param>
        /// <param name="num">The <c>num</c> table.</param>
        /// <param name="add">The <c>add</c> table.</param>
        /// <param name="uf">The union-find.</param>
        /// <param name="idCounter">The id counter.</param>
        /// <param name="strategy">The planning strategy of every rule.</param>
        /// <param name="n">The number of leaves.</param>
        /// <param name="wholeRecentRange">Whether commutativity matches the whole recent range.</param>
        /// <returns>The two roots, which must end up equal.</returns>
        public static (uint LeftRoot, uint RightRoot) Run(Database db, TableId num, TableId add, TableId uf, CounterId idCounter, PlanStrategy strategy, int n, bool wholeRecentRange = false)
        {
            var bases = new BaseValues();
            bases.RegisterType<Int64Codec, long>();
            var ids = new List<uint>();
            for (var i = 0; i < n; i++)
            {
                var id = (uint)db.IncrementCounter(idCounter);
                var v = bases.Get<Int64Codec, long>(i).Rep;
                ids.Add(v);
                DatabaseTestUtil.Stage(db, num, DatabaseTestUtil.R(v, id, 0));
            }

            db.MergeAll();
            uint leftRoot = 0, rightRoot = 0;
            var prev = ids[0];
            foreach (var x in ids.Skip(1))
            {
                var id = (uint)db.IncrementCounter(idCounter);
                DatabaseTestUtil.Stage(db, add, DatabaseTestUtil.R(x, prev, id, 0));
                prev = id;
                leftRoot = id;
            }

            prev = ids[n - 1];
            for (var i = n - 2; i >= 0; i--)
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
                comm.AddAtom(add, [cx, cy, cz, ct], wholeRecentRange
                    ? [Constraint.GeConst(C(3), V(recentStart)), Constraint.LtConst(C(3), V(recentEnd))]
                    : [Constraint.EqConst(C(3), V(recentStart))]);
                var commRules = comm.Build();
                commRules.Insert(add, [cy, cx, cz, V(nextTs)]);
                commRules.Build();
                using var rs = rsb.Build();
                return db.RunRuleSet(rs).Changed;
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
                        changed |= db.RunRuleSet(rs).Changed;
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
                    changed |= db.RunRuleSet(rs).Changed;
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

            return (leftRoot, rightRoot);
        }

        static bool IncrementalRebuild(int ufSize, int tableSize) => ufSize / 4 > tableSize;

    }

}
