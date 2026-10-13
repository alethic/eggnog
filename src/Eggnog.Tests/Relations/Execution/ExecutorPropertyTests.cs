using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Randomized differential tests of the executor: random conjunctive queries (constants, repeated variables,
    /// wildcards, comparisons and column equalities, atoms without variables, every strategy, decomposition on and off,
    /// cached plans with extra header constraints) over random tables with tombstones must produce, through
    /// <see cref="Database.RunRuleSet"/>, exactly the distinct bindings of a brute-force reading of the query as
    /// written. The action records every bound variable through an external function. Matches are a multiset, so only
    /// the distinct bindings are compared.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class ExecutorPropertyTests
    {

        public static IEnumerable<object[]> ModesData => ExecutionTestUtil.Modes.Select(m => new object[] { m });

        /// <summary>
        /// Small tables (keys below 6, so at most 6 live rows in the one-key tables and 36 in the two-key one, values
        /// below 3), up to five atoms.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void SmallDatabasesMatchTheReference(string mode)
        {
            for (var seed = 0; seed < 1500; seed++)
                Check(new Random(seed), mode, seed, rowsPerBatch: 6, keys: 6, values: 3, maxAtoms: 5);
        }

        /// <summary>
        /// Larger tables (keys below 50, up to 180 rows inserted per table, values below 4), up to three atoms, so
        /// candidate subsets cross the executor's size thresholds: sparse column indexes (8 rows), eager refinement (16
        /// rows), child caches, the database's cached indexes for large dense ranges, chunked scans, run-time
        /// reordering and several action batches.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void LargerDatabasesMatchTheReference(string mode)
        {
            for (var seed = 0; seed < 200; seed++)
                Check(new Random(1000 + seed), mode, seed, rowsPerBatch: 60, keys: 50, values: 4, maxAtoms: 3);
        }

        static void Check(Random rng, string mode, int seed, int rowsPerBatch, int keys, int values, int maxAtoms)
        {
            var (db, tables) = RandomDatabase(rng, mode, rowsPerBatch, keys, values);
            var atoms = RandomQuery(rng, tables, values, maxAtoms);
            var strategy = (PlanStrategy)rng.Next(3);
            var noDecomposition = rng.Next(2) == 0;
            var context = $"{mode} seed {seed}, {strategy}, noDecomposition {noDecomposition}: {string.Join("; ", atoms)}";

            var log = new List<uint[]>();
            var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.Strategy = strategy;
            q.NoDecomposition = noDecomposition;
            var variables = Enumerable.Range(0, 5).Select(i => q.NewVariable("v" + i)).ToArray();
            foreach (var atom in atoms)
                q.AddAtom(atom.Table, atom.Entries, atom.Constraints);

            var bound = BoundVariables(atoms);
            var rb = q.Build();
            rb.CallExternal(recorder, bound.Select(v => QueryEntry.Var(v)).ToArray());
            var rule = rb.Build("rule");
            using var rs = rsb.Build();
            var cached = rs.BuildCachedPlan(rule);

            var expected = Reference(db, atoms, [], bound);
            var report = db.RunRuleSet(rs);
            Assert.True(expected.SetEquals(ExecutionTestUtil.Distinct(log)), context + Describe(rs[rule], expected, log));
            Assert.Equal(log.Count, report.NumMatches("rule"));

            // The same rule from its cached plan, with random extra constraints: the semi-naive pattern.
            var extras = RandomExtras(rng, atoms, values);
            log.Clear();
            var rsb2 = db.NewRuleSet();
            var added = rsb2.AddRuleFromCachedPlan(cached, extras.ToArray());
            using var rs2 = rsb2.Build();
            var expectedWithExtras = Reference(db, atoms, extras, bound);
            db.RunRuleSet(rs2);
            if (added is null)
                Assert.True(expectedWithExtras.Count == 0, context + " (cached variant skipped but matches exist)");
            else
                Assert.True(expectedWithExtras.SetEquals(ExecutionTestUtil.Distinct(log)), context + $" with extras {string.Join(", ", extras)}" + Describe(rs2[added.Value], expectedWithExtras, log));
        }

        static string Describe(RulePlan plan, SortedSet<string> expected, List<uint[]> log)
        {
            var got = ExecutionTestUtil.Distinct(log);
            return $"\n{plan.Template}\nexpected {string.Join(" ", expected)}\ngot      {string.Join(" ", got)}";
        }

        static Variable[] BoundVariables(List<RandomAtom> atoms) =>
            atoms.SelectMany(a => a.Entries).Where(e => e.IsVariable && e.IsWildcard == false).Select(e => e.Variable).Distinct().OrderBy(v => v.Index).ToArray();

        /// <summary>
        /// Reads the query as written: every combination of live rows, one per atom, whose cells match the constants,
        /// agree on repeated variables and satisfy the constraints and the extras, projected onto
        /// <paramref name="bound"/>.
        /// </summary>
        static SortedSet<string> Reference(Database db, List<RandomAtom> atoms, List<(AtomId Atom, Constraint Constraint)> extras, Variable[] bound)
        {
            var rows = atoms.Select(a => TableTestUtil.LiveRows(db.GetTable(a.Table))).ToArray();
            var result = new SortedSet<string>(StringComparer.Ordinal);
            var binding = new Dictionary<int, uint>();
            Recurse(0);
            return result;

            void Recurse(int depth)
            {
                if (depth == atoms.Count)
                {
                    result.Add(string.Join(",", bound.Select(v => binding[v.Index])));
                    return;
                }

                var atom = atoms[depth];
                foreach (var row in rows[depth])
                {
                    if (Constraint.EvaluateAll(atom.Constraints, row) == false)
                        continue;
                    if (extras.Any(x => x.Atom.Index == depth && x.Constraint.Evaluate(row) == false))
                        continue;

                    var added = new List<int>();
                    var ok = true;
                    for (var c = 0; c < row.Length && ok; c++)
                    {
                        var e = atom.Entries[c];
                        if (e.IsVariable == false)
                        {
                            ok = row[c] == e.Constant.Rep;
                        }
                        else if (e.IsWildcard == false)
                        {
                            if (binding.TryGetValue(e.Variable.Index, out var v))
                            {
                                ok = v == row[c];
                            }
                            else
                            {
                                binding[e.Variable.Index] = row[c];
                                added.Add(e.Variable.Index);
                            }
                        }
                    }

                    if (ok)
                        Recurse(depth + 1);

                    foreach (var v in added)
                        binding.Remove(v);
                }
            }
        }

        /// <summary>
        /// Three tables: <c>t0(k; a, ts)</c> and <c>t1(k1, k2; ts)</c> sorted by their last column, and an unsorted
        /// <c>t2(k; a)</c>; rows added in timestamp batches, some removed again so tombstones remain, some re-inserted
        /// with new values so superseded rows remain, and the cached indexes sometimes built in between, so that they
        /// list tombstones.
        /// </summary>
        static (Database Db, (TableId Id, int Arity, int SortColumn)[] Tables) RandomDatabase(Random rng, string mode, int rowsPerBatch, int keySpace, int values)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var t0 = db.AddTable(new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            var t1 = db.AddTable(new SortedWritesTable(2, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            var t2 = db.AddTable(new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            for (uint ts = 0; ts < 3; ts++)
            {
                for (var i = rng.Next(rowsPerBatch); i > 0; i--)
                {
                    DatabaseTestUtil.Stage(db, t0, DatabaseTestUtil.R((uint)rng.Next(keySpace), (uint)rng.Next(values), ts));
                    DatabaseTestUtil.Stage(db, t1, DatabaseTestUtil.R((uint)rng.Next(keySpace), (uint)rng.Next(values), ts));
                    DatabaseTestUtil.Stage(db, t2, DatabaseTestUtil.R((uint)rng.Next(keySpace), (uint)rng.Next(values)));
                }

                db.MergeAll();

                // Build the database's cached indexes now and then, as earlier runs would: later deletions and updates
                // leave tombstones listed in them (they refresh incrementally).
                if (rng.Next(2) == 0)
                {
                    foreach (var (table, arity) in new[] { (t0, 3), (t1, 3), (t2, 2) })
                    {
                        var info = db.GetTableInfo(table);
                        for (var c = 0; c < arity; c++)
                            info.GetColumnIndex(C(c));

                        info.GetIndex([C(0), C(1)]);
                        info.GetIndex([C(1), C(0)]);
                    }
                }

                for (var k = rng.Next(3); k > 0; k--)
                {
                    DatabaseTestUtil.StageRemove(db, t0, DatabaseTestUtil.R((uint)rng.Next(keySpace)));
                    DatabaseTestUtil.StageRemove(db, t2, DatabaseTestUtil.R((uint)rng.Next(keySpace)));
                    db.MergeAll();
                }
            }

            return (db, [(t0, 3, 2), (t1, 3, 2), (t2, 2, -1)]);
        }

        static List<RandomAtom> RandomQuery(Random rng, (TableId Id, int Arity, int SortColumn)[] tables, int values, int maxAtoms)
        {
            var atoms = new List<RandomAtom>();
            for (var n = 1 + rng.Next(maxAtoms); n > 0; n--)
            {
                var (table, arity, sort) = tables[rng.Next(tables.Length)];
                var entries = new QueryEntry[arity];
                for (var c = 0; c < arity; c++)
                {
                    var roll = rng.Next(10);
                    entries[c] = roll < 7 ? QueryEntry.Var(new Variable(rng.Next(5))) : roll < 8 ? QueryEntry.Const(V((uint)rng.Next(values))) : QueryEntry.Wildcard();
                }

                var constraints = new List<Constraint>();
                for (var k = rng.Next(10) switch { < 6 => 0, < 9 => 1, _ => 2 }; k > 0; k--)
                    constraints.Add(RandomConstraint(rng, arity, sort, values, allowEq: true));

                atoms.Add(new RandomAtom(table, entries, constraints.ToArray()));
            }

            return atoms;
        }

        static List<(AtomId Atom, Constraint Constraint)> RandomExtras(Random rng, List<RandomAtom> atoms, int values)
        {
            var extras = new List<(AtomId, Constraint)>();
            for (var k = rng.Next(3); k > 0; k--)
            {
                var i = rng.Next(atoms.Count);
                var arity = atoms[i].Entries.Length;

                // Cached plans accept only fast constraints: ranges on the sort column, or equalities (promoted).
                var sorted = arity == 3;
                var c = sorted && rng.Next(3) != 0
                    ? RandomConstraint(rng, arity, 2, values, allowEq: false, onlySort: true)
                    : Constraint.EqConst(C(rng.Next(arity)), V((uint)rng.Next(values)));
                extras.Add((new AtomId(i), c));
            }

            return extras;
        }

        static Constraint RandomConstraint(Random rng, int arity, int sort, int values, bool allowEq, bool onlySort = false)
        {
            var column = onlySort ? sort : rng.Next(arity);
            var value = V((uint)rng.Next(onlySort ? 3 : values));
            switch (rng.Next(allowEq ? 6 : 5))
            {
                case 0:
                    return Constraint.EqConst(C(column), value);
                case 1:
                    return Constraint.LtConst(C(column), value);
                case 2:
                    return Constraint.GtConst(C(column), value);
                case 3:
                    return Constraint.LeConst(C(column), value);
                case 4:
                    return Constraint.GeConst(C(column), value);
                default:
                    return Constraint.Eq(C(column), C(rng.Next(arity)));
            }
        }

    }

}
