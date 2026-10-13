using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The executor against a reference: several random rules in one rule set (the serial executor reuses one join and
    /// one action buffer for all of them), wider tables and more variables than the single-rule property test, and
    /// early stops in decomposed and multi-rule sets.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class MultiRuleExecutorPropertyTests
    {

        public static IEnumerable<object[]> ModesData => ExecutionTestUtil.Modes.Select(m => new object[] { m });

        [Theory]
        [MemberData(nameof(ModesData))]
        public void SeveralRulesInOneRuleSetMatchTheReference(string mode)
        {
            for (var seed = 0; seed < 100; seed++)
                CheckMany(new Random(5000 + seed), mode, seed, rowsPerBatch: 60, keys: 40, values: 4, maxAtoms: 4, rules: 2 + seed % 4);
        }

        [Theory]
        [MemberData(nameof(ModesData))]
        public void LargeTablesManyVariablesMatchTheReference(string mode)
        {
            for (var seed = 0; seed < 20; seed++)
                CheckMany(new Random(9000 + seed), mode, seed, rowsPerBatch: 250, keys: 120, values: 3, maxAtoms: 3, rules: 1 + seed % 2);
        }

        [Theory]
        [MemberData(nameof(ModesData))]
        public void EarlyStopInDecomposedAndMultiRuleSets(string mode)
        {
            for (var seed = 0; seed < 40; seed++)
            {
                var db = ExecutionTestUtil.NewDatabase(mode);
                var tables = Enumerable.Range(0, 4).Select(_ => db.AddTable(Strict(2, 2))).ToArray();
                var rng = new Random(seed);
                foreach (var t in tables)
                    DatabaseTestUtil.Stage(db, t, Enumerable.Range(0, 400).Select(_ => new[] { (uint)rng.Next(60), (uint)rng.Next(60) }).ToArray());

                db.MergeAll();
                var calls = 0;
                var limit = 1 + rng.Next(3000);
                var stop = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
                {
                    if (System.Threading.Interlocked.Increment(ref calls) >= limit)
                        state.TriggerEarlyStop();

                    result = 0;
                    return true;
                }));

                var rsb = db.NewRuleSet();
                for (var r = 0; r < 3; r++)
                {
                    var q = rsb.NewRule();
                    q.Strategy = (PlanStrategy)rng.Next(3);
                    q.NoDecomposition = rng.Next(2) == 0;
                    var v = Enumerable.Range(0, 5).Select(i => q.NewVariable("v" + i)).ToArray();
                    for (var a = 0; a < 4; a++)
                        q.AddAtom(tables[a], [v[a], v[a + 1]]);

                    var rb = q.Build();
                    rb.CallExternal(stop, [v[0], v[4]]);
                    rb.Build("r" + r);
                }

                using var rs = rsb.Build();
                var report = db.RunRuleSet(rs);
                Assert.True(calls >= Math.Min(limit, 1), $"{mode} seed {seed}");
                Assert.True(calls < limit + 100_000, $"{mode} seed {seed}: {calls} calls for a stop at {limit}");
            }
        }

        static void CheckMany(Random rng, string mode, int seed, int rowsPerBatch, int keys, int values, int maxAtoms, int rules)
        {
            var (db, tables) = RandomDatabase(rng, mode, rowsPerBatch, keys, values);
            var rsb = db.NewRuleSet();
            var logs = new List<List<uint[]>>();
            var queries = new List<(List<RandomAtom> Atoms, Variable[] Bound, string Context)>();
            for (var r = 0; r < rules; r++)
            {
                var atoms = RandomQuery(rng, tables, values, maxAtoms);
                var strategy = (PlanStrategy)rng.Next(3);
                var noDecomposition = rng.Next(2) == 0;
                var log = new List<uint[]>();
                logs.Add(log);
                var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
                var q = rsb.NewRule();
                q.Strategy = strategy;
                q.NoDecomposition = noDecomposition;
                for (var i = 0; i < 6; i++)
                    q.NewVariable("v" + i);

                foreach (var atom in atoms)
                    q.AddAtom(atom.Table, atom.Entries, atom.Constraints);

                var bound = atoms.SelectMany(a => a.Entries).Where(e => e.IsVariable && e.IsWildcard == false).Select(e => e.Variable).Distinct().OrderBy(v => v.Index).ToArray();
                var rb = q.Build();
                rb.CallExternal(recorder, bound.Select(v => QueryEntry.Var(v)).ToArray());
                rb.Build("rule" + r);
                queries.Add((atoms, bound, $"{mode} seed {seed} rule {r}, {strategy}, noDecomposition {noDecomposition}: {string.Join("; ", atoms)}"));
            }

            using var rs = rsb.Build();
            var expected = queries.Select(q => Reference(db, q.Atoms, q.Bound)).ToList();
            db.RunRuleSet(rs);
            for (var r = 0; r < rules; r++)
            {
                var got = ExecutionTestUtil.Distinct(logs[r]);
                Assert.True(expected[r].SetEquals(got), queries[r].Context + $"\n{rs[new RuleId(r)].Template}\nexpected {string.Join(" ", expected[r])}\ngot      {string.Join(" ", got)}");
            }
        }

        static SortedSet<string> Reference(Database db, List<RandomAtom> atoms, Variable[] bound)
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

        static (Database Db, (TableId Id, int Arity, int SortColumn)[] Tables) RandomDatabase(Random rng, string mode, int rowsPerBatch, int keySpace, int values)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var t0 = db.AddTable(new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            var t1 = db.AddTable(new SortedWritesTable(2, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            var t2 = db.AddTable(new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            var t3 = db.AddTable(new SortedWritesTable(2, 4, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            for (uint ts = 0; ts < 3; ts++)
            {
                for (var i = rng.Next(rowsPerBatch); i > 0; i--)
                {
                    DatabaseTestUtil.Stage(db, t0, DatabaseTestUtil.R((uint)rng.Next(keySpace), (uint)rng.Next(values), ts));
                    DatabaseTestUtil.Stage(db, t1, DatabaseTestUtil.R((uint)rng.Next(keySpace), (uint)rng.Next(values), ts));
                    DatabaseTestUtil.Stage(db, t2, DatabaseTestUtil.R((uint)rng.Next(keySpace), (uint)rng.Next(values)));
                    DatabaseTestUtil.Stage(db, t3, DatabaseTestUtil.R((uint)rng.Next(values + 2), (uint)rng.Next(keySpace), (uint)rng.Next(values), (uint)rng.Next(values)));
                }

                db.MergeAll();
                if (rng.Next(2) == 0)
                {
                    foreach (var (table, arity) in new[] { (t0, 3), (t1, 3), (t2, 2), (t3, 4) })
                    {
                        var info = db.GetTableInfo(table);
                        for (var c = 0; c < arity; c++)
                            info.GetColumnIndex(C(c));

                        info.GetIndex([C(0), C(1)]);
                        info.GetIndex([C(1), C(0)]);
                    }
                }

                for (var k = rng.Next(4); k > 0; k--)
                {
                    DatabaseTestUtil.StageRemove(db, t0, DatabaseTestUtil.R((uint)rng.Next(keySpace)));
                    DatabaseTestUtil.StageRemove(db, t2, DatabaseTestUtil.R((uint)rng.Next(keySpace)));
                    DatabaseTestUtil.StageRemove(db, t3, DatabaseTestUtil.R((uint)rng.Next(values + 2), (uint)rng.Next(keySpace)));
                    db.MergeAll();
                }
            }

            return (db, [(t0, 3, 2), (t1, 3, 2), (t2, 2, -1), (t3, 4, -1)]);
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
                    entries[c] = roll < 7 ? QueryEntry.Var(new Variable(rng.Next(6))) : roll < 8 ? QueryEntry.Const(V((uint)rng.Next(values))) : QueryEntry.Wildcard();
                }

                var constraints = new List<Constraint>();
                for (var k = rng.Next(10) switch { < 7 => 0, _ => 1 }; k > 0; k--)
                {
                    var column = rng.Next(arity);
                    var value = V((uint)rng.Next(values));
                    constraints.Add(rng.Next(4) switch
                    {
                        0 => Constraint.LtConst(C(column), value),
                        1 => Constraint.GtConst(C(column), value),
                        2 => Constraint.EqConst(C(column), value),
                        _ => Constraint.Eq(C(column), C(rng.Next(arity))),
                    });
                }

                atoms.Add(new RandomAtom(table, entries, constraints.ToArray()));
            }

            return atoms;
        }

    }

}
