using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Randomized differential tests of rule building: random conjunctive queries (constants, repeated variables,
    /// wildcards, comparisons and column equalities, every strategy, decomposition on and off) over random tables with
    /// tombstones, built through <see cref="RuleQueryBuilder"/>, must denote the same bindings as a brute-force reading
    /// of the query as written. The rule's denotation is what its atoms and header subsets say
    /// (<see cref="NaiveRuleRunner.Enumerate"/>), so this checks the translation of entries into constraints, the
    /// fast/slow split, the header evaluation and cached-plan instantiation with extra constraints.
    /// </summary>
    public class RuleQueryPropertyTests
    {

        const int Values = 4;

        [Fact]
        public void RulesDenoteTheirQueries()
        {
            for (var seed = 0; seed < 500; seed++)
            {
                var rng = new Random(seed);
                var (db, tables) = RandomDatabase(rng);
                var atoms = RandomQuery(rng, tables);
                var strategy = (PlanStrategy)rng.Next(3);
                var noDecomposition = rng.Next(2) == 0;

                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                q.Strategy = strategy;
                q.NoDecomposition = noDecomposition;
                var variables = Enumerable.Range(0, 5).Select(i => q.NewVariable("v" + i)).ToArray();
                foreach (var atom in atoms)
                    q.AddAtom(atom.Table, atom.Entries, atom.Constraints);

                var rb = q.Build();
                var read = atoms.SelectMany(a => a.Entries).Where(e => e.IsVariable && e.IsWildcard == false).Select(e => e.Variable).Distinct().ToArray();
                if (read.Length > 0)
                    rb.AssertEq(read[0], read[0]);

                var rule = rb.Build();
                using var rs = rsb.Build();
                var context = $"seed {seed}, {strategy}, noDecomposition {noDecomposition}: {string.Join("; ", atoms)}";

                var expected = Reference(db, atoms, []);
                Assert.True(expected.SetEquals(Denotation(db, rs[rule])), context);

                // The same rule from its cached plan, with random extra constraints.
                var cached = rs.BuildCachedPlan(rule);
                var extras = RandomExtras(rng, atoms);
                var rsb2 = db.NewRuleSet();
                var added = rsb2.AddRuleFromCachedPlan(cached, extras.ToArray());
                using var rs2 = rsb2.Build();
                var expectedWithExtras = Reference(db, atoms, extras);
                if (added is null)
                {
                    Assert.True(expectedWithExtras.Count == 0, context + " (cached variant skipped but matches exist)");
                    Assert.Equal(0, rs2.Count);
                }
                else
                {
                    Assert.True(expectedWithExtras.SetEquals(Denotation(db, rs2[added.Value])), context + $" with extras {string.Join(", ", extras)}");
                }
            }
        }

        static HashSet<string> Denotation(Database db, RulePlan plan)
        {
            if (plan.Instance is null)
                return new HashSet<string>();

            var (bound, bindings) = NaiveRuleRunner.Enumerate(db, plan);
            return bindings.Select(b => Key(bound.Select(v => (v.Index, b[v.Index])))).ToHashSet();
        }

        /// <summary>
        /// Reads the query as written: every combination of live rows, one per atom, whose cells match the constants,
        /// agree on repeated variables and satisfy the constraints and the extras.
        /// </summary>
        static HashSet<string> Reference(Database db, List<RandomAtom> atoms, List<(AtomId Atom, Constraint Constraint)> extras)
        {
            var rows = atoms.Select(a => TableTestUtil.LiveRows(db.GetTable(a.Table))).ToArray();
            var result = new HashSet<string>();
            var binding = new Dictionary<int, uint>();
            Recurse(0);
            return result;

            void Recurse(int depth)
            {
                if (depth == atoms.Count)
                {
                    result.Add(Key(binding.Select(kv => (kv.Key, kv.Value))));
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

        static string Key(IEnumerable<(int Variable, uint Value)> binding) => string.Join(",", binding.OrderBy(b => b.Variable).Select(b => $"{b.Variable}={b.Value}"));

        /// <summary>
        /// Three tables: <c>t0(k; a, ts)</c> and <c>t1(k1, k2; ts)</c> sorted by their last column, and an unsorted
        /// <c>t2(k; a)</c>; rows added in timestamp batches, some removed again so tombstones remain.
        /// </summary>
        static (Database Db, (TableId Id, int Arity, int SortColumn)[] Tables) RandomDatabase(Random rng)
        {
            var db = new Database(ParallelHeuristics.Serial);
            var t0 = db.AddTable(new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            var t1 = db.AddTable(new SortedWritesTable(2, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            var t2 = db.AddTable(new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            for (uint ts = 0; ts < 3; ts++)
            {
                for (var i = rng.Next(5); i > 0; i--)
                {
                    DatabaseTestUtil.Stage(db, t0, DatabaseTestUtil.R((uint)rng.Next(Values), (uint)rng.Next(Values), ts));
                    DatabaseTestUtil.Stage(db, t1, DatabaseTestUtil.R((uint)rng.Next(Values), (uint)rng.Next(Values), ts));
                    DatabaseTestUtil.Stage(db, t2, DatabaseTestUtil.R((uint)rng.Next(Values), (uint)rng.Next(Values)));
                }

                db.MergeAll();
                if (rng.Next(3) == 0)
                {
                    DatabaseTestUtil.StageRemove(db, t0, DatabaseTestUtil.R((uint)rng.Next(Values)));
                    DatabaseTestUtil.StageRemove(db, t2, DatabaseTestUtil.R((uint)rng.Next(Values)));
                    db.MergeAll();
                }
            }

            return (db, [(t0, 3, 2), (t1, 3, 2), (t2, 2, -1)]);
        }

        static List<RandomAtom> RandomQuery(Random rng, (TableId Id, int Arity, int SortColumn)[] tables)
        {
            var atoms = new List<RandomAtom>();
            for (var n = 1 + rng.Next(4); n > 0; n--)
            {
                var (table, arity, sort) = tables[rng.Next(tables.Length)];
                var entries = new QueryEntry[arity];
                for (var c = 0; c < arity; c++)
                {
                    var roll = rng.Next(10);
                    entries[c] = roll < 7 ? QueryEntry.Var(new Variable(rng.Next(5))) : roll < 8 ? QueryEntry.Const(V((uint)rng.Next(Values))) : QueryEntry.Wildcard();
                }

                var constraints = new List<Constraint>();
                // Half the atoms get no constraint, so that enough queries have matches.
                for (var k = rng.Next(10) switch { < 5 => 0, < 8 => 1, _ => 2 }; k > 0; k--)
                    constraints.Add(RandomConstraint(rng, arity, sort, allowEq: true));

                atoms.Add(new RandomAtom(table, entries, constraints.ToArray()));
            }

            return atoms;
        }

        static List<(AtomId Atom, Constraint Constraint)> RandomExtras(Random rng, List<RandomAtom> atoms)
        {
            var extras = new List<(AtomId, Constraint)>();
            for (var k = rng.Next(3); k > 0; k--)
            {
                var i = rng.Next(atoms.Count);
                var arity = atoms[i].Entries.Length;

                // Cached plans accept only fast constraints: ranges on the sort column, or equalities (promoted).
                var sorted = arity == 3;
                var c = sorted && rng.Next(2) == 0
                    ? RandomConstraint(rng, arity, 2, allowEq: false, onlySort: true)
                    : Constraint.EqConst(C(rng.Next(arity)), V((uint)rng.Next(Values)));
                extras.Add((new AtomId(i), c));
            }

            return extras;
        }

        static Constraint RandomConstraint(Random rng, int arity, int sort, bool allowEq, bool onlySort = false)
        {
            var column = onlySort ? sort : rng.Next(arity);
            var value = V((uint)rng.Next(Values));
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
