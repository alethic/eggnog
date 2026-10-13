using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// egglog's <c>ac_test_inner</c> (core-relations/src/tests.rs:513-951) on <see cref="Database.RunRuleSet"/>, the
    /// benchmark counterpart of the test project's <c>AcSaturation</c>: egglog reimplemented on the engine (rule-based
    /// rebuilding with incremental and nonincremental variants chosen by size estimates, semi-naive evaluation with
    /// timestamp ranges on the sorted column, and iteration until nothing changes) on the associativity and
    /// commutativity workload, over a <see cref="MathEgraph"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Load"/> builds <c>(0 + ... + n-1)</c> left-associated and <c>(n-1 + ... + 0)</c> right-associated, as
    /// the test does before its loop; <see cref="Saturate"/> is the loop: the AC rules over the recent timestamp range,
    /// then the rebuild rule sets until they change nothing, until the AC rules change nothing.
    /// </para>
    /// <para>
    /// Two choices differ from the test's defaults, both offered by it: commutativity matches the whole recent range
    /// (the test's <c>wholeRecentRange: true</c>), which saturates, so the serial and parallel modes do the same work up
    /// to renaming and the result is known exactly (<see cref="SaturatedRows"/>, <see cref="SaturatedClasses"/>); and
    /// each rule is planned as egglog's bridge plans it (<see cref="PlanStrategy.Gj"/> for the two-atom associativity
    /// rules, <see cref="PlanStrategy.MinCover"/> for the single-atom commutativity rule and the rebuild rules), where
    /// the test takes one strategy for every rule.
    /// </para>
    /// </remarks>
    internal sealed class AcSaturationWorkload
    {

        /// <summary>
        /// The bound on AC iterations; the test asserts saturation takes fewer than 100.
        /// </summary>
        const int MaxIterations = 100;

        readonly BaseValues bases;

        /// <summary>
        /// Initializes the workload for <paramref name="leaves"/> leaves.
        /// </summary>
        /// <param name="leaves">The number of leaves <c>n</c>; at least two.</param>
        public AcSaturationWorkload(int leaves)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(leaves, 2);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(leaves, 19);

            Leaves = leaves;
            bases = new BaseValues();
            bases.RegisterType<Int64Codec, long>();
        }

        /// <summary>
        /// Gets the number of leaves <c>n</c>.
        /// </summary>
        public int Leaves { get; }

        /// <summary>
        /// Gets the number of live <c>add</c> rows of the saturated e-graph over <paramref name="leaves"/> leaves: one
        /// row per ordered split of every set of two or more leaves into two nonempty parts,
        /// <c>sum over k of C(n, k) (2^k - 2) = 3^n - 2^(n+1) + 1</c>.
        /// </summary>
        /// <param name="leaves">The number of leaves.</param>
        /// <returns>The row count.</returns>
        public static long SaturatedRows(int leaves)
        {
            long power3 = 1;
            for (var i = 0; i < leaves; i++)
                power3 *= 3;

            return power3 - (2L << leaves) + 1;
        }

        /// <summary>
        /// Gets the number of e-classes the saturated <c>add</c> table's rows produce over <paramref name="leaves"/>
        /// leaves: one per set of two or more leaves, <c>2^n - n - 1</c> (the test's
        /// <c>SaturationIsTheSameInEveryMode</c> asserts it).
        /// </summary>
        /// <param name="leaves">The number of leaves.</param>
        /// <returns>The class count.</returns>
        public static long SaturatedClasses(int leaves) => (1L << leaves) - leaves - 1;

        /// <summary>
        /// Gets the number of leaves whose saturated <c>add</c> table is nearest, by ratio, to <paramref name="rows"/>
        /// rows: 9 leaves (18,660 rows) for 20,000 and 11 leaves (173,052 rows) for 200,000. The AC e-graph grows by
        /// about three times per leaf, so a row count is matched to the nearest size the workload has.
        /// </summary>
        /// <param name="rows">The row count wanted; at least one.</param>
        /// <returns>The number of leaves, at least two.</returns>
        public static int LeavesFor(int rows)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

            var best = 2;
            var bestDistance = double.MaxValue;
            for (var n = 2; n <= 19; n++)
            {
                var distance = Math.Abs(Math.Log((double)SaturatedRows(n) / rows));
                if (distance < bestDistance)
                {
                    best = n;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>
        /// Loads the input into <paramref name="graph"/>: <c>num(i, id_i, 0)</c> for each leaf, then the left- and
        /// right-associated sums as <c>add</c> rows stamped 0, merging after each part as the test does.
        /// </summary>
        /// <param name="graph">An empty fixture.</param>
        /// <returns>The ids of the two sums, which saturation must make equal.</returns>
        public (uint LeftRoot, uint RightRoot) Load(MathEgraph graph)
        {
            ArgumentNullException.ThrowIfNull(graph);

            var db = graph.Db;
            var n = Leaves;
            var ids = new List<uint>(n);
            var numBuffer = db.NewBuffer(graph.Num);
            for (var i = 0; i < n; i++)
            {
                var id = (uint)db.IncrementCounter(graph.IdCounter);
                var v = bases.Get<Int64Codec, long>(i).Rep;
                ids.Add(v);
                numBuffer.StageInsert([v, id, 0]);
            }

            numBuffer.Flush();
            db.MergeAll();

            uint leftRoot = 0, rightRoot = 0;
            var addBuffer = db.NewBuffer(graph.Add);
            var prev = ids[0];
            for (var i = 1; i < n; i++)
            {
                var id = (uint)db.IncrementCounter(graph.IdCounter);
                addBuffer.StageInsert([ids[i], prev, id, 0]);
                prev = id;
                leftRoot = id;
            }

            prev = ids[n - 1];
            for (var i = n - 2; i >= 0; i--)
            {
                var id = (uint)db.IncrementCounter(graph.IdCounter);
                addBuffer.StageInsert([prev, ids[i], id, 0]);
                prev = id;
                rightRoot = id;
            }

            addBuffer.Flush();
            db.MergeAll();
            return (leftRoot, rightRoot);
        }

        /// <summary>
        /// Saturates associativity and commutativity, rebuilding after every iteration: the test's loop.
        /// </summary>
        /// <param name="graph">A fixture <see cref="Load"/> filled.</param>
        /// <returns>The number of AC iterations that changed something.</returns>
        /// <exception cref="InvalidOperationException">Saturation takes <see cref="MaxIterations"/> iterations or more.</exception>
        public int Saturate(MathEgraph graph)
        {
            ArgumentNullException.ThrowIfNull(graph);

            uint cur = 0, next = 1;
            var iterations = 0;
            while (RunAcRule(graph, cur, next))
            {
                if (++iterations >= MaxIterations)
                    throw new InvalidOperationException($"AC saturation of {Leaves} leaves did not finish in {MaxIterations} iterations.");

                var start = next;
                var newIdsAt = start;
                var changed = true;
                while (changed)
                    (newIdsAt, changed) = Rebuild(graph, newIdsAt);

                cur = start;
                next = newIdsAt + 1;
            }

            return iterations;
        }

        /// <summary>
        /// Checks that <paramref name="graph"/> holds the saturated e-graph: the two sums are equal, the live
        /// <c>add</c> rows are canonical and number <see cref="SaturatedRows"/>, and their outputs fall into
        /// <see cref="SaturatedClasses"/> classes. O(rows).
        /// </summary>
        /// <param name="graph">The fixture after <see cref="Saturate"/>.</param>
        /// <param name="roots">The ids <see cref="Load"/> returned.</param>
        /// <exception cref="InvalidOperationException">The e-graph is not the saturated one.</exception>
        public void Verify(MathEgraph graph, (uint LeftRoot, uint RightRoot) roots)
        {
            ArgumentNullException.ThrowIfNull(graph);

            var left = graph.Find(roots.LeftRoot);
            var right = graph.Find(roots.RightRoot);
            if (left != right)
                throw new InvalidOperationException($"After AC saturation of {Leaves} leaves the two sums are in classes {left} and {right}.");

            var rows = RowCollector.LiveRows(graph.Db.GetTable(graph.Add));
            var classes = new HashSet<uint>();
            foreach (var row in rows)
            {
                for (var c = 0; c < 3; c++)
                    if (graph.Find(row[c]) != row[c])
                        throw new InvalidOperationException($"The saturated add row ({string.Join(", ", row)}) holds the non-canonical id {row[c]}.");

                classes.Add(row[2]);
            }

            if (rows.Count != SaturatedRows(Leaves) || classes.Count != SaturatedClasses(Leaves))
                throw new InvalidOperationException(
                    $"AC saturation of {Leaves} leaves left {rows.Count} add rows in {classes.Count} classes; "
                    + $"the saturated e-graph has {SaturatedRows(Leaves)} rows in {SaturatedClasses(Leaves)} classes.");
        }

        /// <summary>
        /// Runs associativity (both semi-naive variants) and commutativity over the recent range
        /// [<paramref name="recentStart"/>, <paramref name="recentEnd"/>), stamping new rows <paramref name="recentEnd"/>.
        /// </summary>
        /// <returns>Whether the run changed anything.</returns>
        static bool RunAcRule(MathEgraph graph, uint recentStart, uint recentEnd)
        {
            var db = graph.Db;
            var add = graph.Add;
            var nextTs = recentEnd;
            var rsb = new RuleSetBuilder(db);
            Span<(uint LStart, uint LEnd, uint RStart, uint REnd)> variants =
            [
                (0u, recentEnd, recentStart, recentEnd),
                (recentStart, recentEnd, 0u, recentStart),
            ];
            foreach (var (lStart, lEnd, rStart, rEnd) in variants)
            {
                // Add(x, Add(y, z)) => Add(Add(x, y), z)
                var assoc = rsb.NewRule();
                assoc.Strategy = PlanStrategy.Gj;
                var x = assoc.NewVariable("x");
                var y = assoc.NewVariable("y");
                var z = assoc.NewVariable("z");
                var i1 = assoc.NewVariable("i1");
                var i2 = assoc.NewVariable("i2");
                var t1 = assoc.NewVariable("t1");
                var t2 = assoc.NewVariable("t2");
                assoc.AddAtom(add, [y, z, i1, t1], [Constraint.GeConst(new ColumnId(3), lStart), Constraint.LtConst(new ColumnId(3), lEnd)]);
                assoc.AddAtom(add, [x, i1, i2, t2], [Constraint.GeConst(new ColumnId(3), rStart), Constraint.LtConst(new ColumnId(3), rEnd)]);
                var rules = assoc.Build();
                var res = rules.LookupOrInsert(add, [x, y], [WriteVal.FromCounter(graph.IdCounter), new Value(nextTs)], new ColumnId(2));
                rules.Insert(add, [res, z, i2, new Value(nextTs)]);
                rules.Build();
            }

            // Add(x, y, z, t1) => Add(y, x, z, next), over the whole recent range.
            var comm = rsb.NewRule();
            comm.Strategy = PlanStrategy.MinCover;
            var cx = comm.NewVariable("x");
            var cy = comm.NewVariable("y");
            var cz = comm.NewVariable("z");
            var ct = comm.NewVariable("t1");
            comm.AddAtom(
                add,
                [cx, cy, cz, ct],
                [Constraint.GeConst(new ColumnId(3), recentStart), Constraint.LtConst(new ColumnId(3), recentEnd)]);
            var commRules = comm.Build();
            commRules.Insert(add, [cy, cx, cz, new Value(nextTs)]);
            commRules.Build();
            using var rs = rsb.Build();
            return db.RunRuleSet(rs).Changed;
        }

        /// <summary>
        /// Adds the rule that rebuilds <c>num</c>'s id column: nonincrementally (look every id up in the union-find)
        /// when the union-find's recent rows are many against the table, incrementally (join the recent unions)
        /// otherwise.
        /// </summary>
        static void NumRebuild(MathEgraph graph, RuleSetBuilder rsb, uint curTs, uint nextTs)
        {
            var num = graph.Num;
            var uf = graph.Uf;
            var numSize = rsb.EstimateSize(num);
            var ufSize = rsb.EstimateSize(uf, Constraint.EqConst(new ColumnId(2), curTs));
            var q = rsb.NewRule();
            q.Strategy = PlanStrategy.MinCover;
            if (IncrementalRebuild(ufSize, numSize))
            {
                // Nonincremental: num(x, id, t1) => num(x, id', next) where id' is canonical.
                var x = q.NewVariable("x");
                var id = q.NewVariable("id");
                var t1 = q.NewVariable("t1");
                q.AddAtom(num, [x, id, t1]);
                var rules = q.Build();
                var canon = rules.LookupWithDefault(uf, [id], id, new ColumnId(1));
                rules.AssertNe(id, canon);
                rules.Insert(num, [x, canon, new Value(nextTs)]);
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
                q.AddAtom(uf, [id, idNew, t2], [Constraint.EqConst(new ColumnId(2), curTs)]);
                var rules = q.Build();
                rules.Insert(num, [x, idNew, new Value(nextTs)]);
                rules.Build();
            }
        }

        /// <summary>
        /// Adds the incremental rule that rebuilds the <c>add</c> rows whose column <paramref name="joined"/> (0 the
        /// left operand, 1 the right, 2 the id) lost a union at <paramref name="curTs"/>.
        /// </summary>
        static void AddRebuild(MathEgraph graph, RuleSetBuilder rsb, uint curTs, uint nextTs, int joined)
        {
            var add = graph.Add;
            var uf = graph.Uf;
            var q = rsb.NewRule();
            q.Strategy = PlanStrategy.MinCover;
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var id = q.NewVariable("id");
            var t1 = q.NewVariable("t1");
            var fresh = q.NewVariable(joined switch { 0 => "x_new", 1 => "y_new", _ => "id_new" });
            var t2 = q.NewVariable("t2");
            q.AddAtom(add, [x, y, id, t1]);
            q.AddAtom(uf, [joined switch { 0 => x, 1 => y, _ => id }, fresh, t2], [Constraint.EqConst(new ColumnId(2), curTs)]);
            var rules = q.Build();
            var xNew = joined == 0 ? fresh : rules.LookupWithDefault(uf, [x], x, new ColumnId(1));
            var yNew = joined == 1 ? fresh : rules.LookupWithDefault(uf, [y], y, new ColumnId(1));
            var idNew = joined == 2 ? fresh : rules.LookupWithDefault(uf, [id], id, new ColumnId(1));
            rules.Remove(add, [x, y]);
            rules.Insert(add, [xNew, yNew, idNew, new Value(nextTs)]);
            rules.Build();
        }

        /// <summary>
        /// One rule-based rebuild pass at <paramref name="curTs"/>: <c>num</c> and <c>add</c>, incrementally (the id
        /// column first, then the left, then the right operand, as egglog orders them) or nonincrementally.
        /// </summary>
        /// <returns>The next pass's timestamp, and whether this pass changed anything.</returns>
        static (uint Next, bool Changed) Rebuild(MathEgraph graph, uint curTs)
        {
            var db = graph.Db;
            var add = graph.Add;
            var uf = graph.Uf;
            var nextTs = curTs + 1;
            var rsb = db.NewRuleSet();
            NumRebuild(graph, rsb, curTs, nextTs);
            var changed = false;
            var addSize = rsb.EstimateSize(add);
            var ufSize = rsb.EstimateSize(uf, Constraint.EqConst(new ColumnId(2), curTs));
            if (IncrementalRebuild(ufSize, addSize))
            {
                // egglog's order: the id column first (with num), then the left, then the right operand.
                ReadOnlySpan<int> order = [2, 0, 1];
                foreach (var joined in order)
                {
                    if (joined != 2)
                    {
                        rsb = db.NewRuleSet();
                        NumRebuild(graph, rsb, curTs, nextTs);
                    }

                    AddRebuild(graph, rsb, curTs, nextTs, joined);
                    using var rs = rsb.Build();
                    changed |= db.RunRuleSet(rs).Changed;
                }
            }
            else
            {
                // Nonincremental: recanonicalize every add row that changes.
                var q = rsb.NewRule();
                q.Strategy = PlanStrategy.MinCover;
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var id = q.NewVariable("id");
                var t1 = q.NewVariable("t1");
                q.AddAtom(add, [x, y, id, t1]);
                var rules = q.Build();
                var xc = rules.LookupWithDefault(uf, [x], x, new ColumnId(1));
                var yc = rules.LookupWithDefault(uf, [y], y, new ColumnId(1));
                var idc = rules.LookupWithDefault(uf, [id], id, new ColumnId(1));
                rules.AssertAnyNe([x, y, id], [xc, yc, idc]);
                rules.Remove(add, [x, y]);
                rules.Insert(add, [xc, yc, idc, new Value(nextTs)]);
                rules.Build();
                using var rs = rsb.Build();
                changed |= db.RunRuleSet(rs).Changed;
            }

            return (nextTs, changed);
        }

        /// <summary>
        /// egglog's <c>incremental_rebuild</c> (core-relations/src/tests.rs:1012-1014), the test's choice between the
        /// rebuild variants. Despite its name, <c>true</c> selects the nonincremental variant: the union-find's recent
        /// rows exceed four times the table.
        /// </summary>
        static bool IncrementalRebuild(int ufSize, int tableSize) => ufSize / 4 > tableSize;

    }

}
