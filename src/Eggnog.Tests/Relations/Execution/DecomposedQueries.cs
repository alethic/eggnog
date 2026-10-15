using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The planner experiments' queries (<see cref="ExperimentQueries"/>) on a real <see cref="Database"/>: binary tables
    /// filled with <see cref="PlanTestDatabase.ExperimentRows"/>, a rule whose action records the experiment's output
    /// variables, and a brute-force reference of the same projection.
    /// </summary>
    static class DecomposedQueries
    {

        /// <summary>
        /// The experiments: tables as (rows, modulus), atoms as (table, variable per column), variable count, output.
        /// </summary>
        static (int N, int M)[] Tables(string name) => name switch
        {
            "path6" => [(100, 30), (10, 5), (50, 20), (20, 10), (70, 9), (40, 13)],
            "tree5" => [(100, 30), (10, 5), (50, 20), (20, 10), (70, 9)],
            "spider" => [(100, 30), (10, 5), (50, 20), (20, 10), (70, 9), (40, 13), (30, 7), (60, 11), (25, 6)],
            "triangle" or "path3" or "star" => [(100, 30), (10, 5), (50, 20)],
            _ => [(100, 30), (10, 5), (50, 20), (20, 10)],
        };

        static ((int Table, int A, int B)[] Atoms, int VarCount, int[] Output) Spec(string name)
        {
            switch (name)
            {
                case "triangle":
                    return ([(0, 0, 1), (1, 1, 2), (2, 2, 0)], 3, [0, 1, 2]);
                case "path3":
                    return ([(0, 0, 1), (1, 1, 2), (2, 2, 3)], 4, [0, 3]);
                case "path4":
                    return ([(0, 0, 1), (1, 1, 2), (2, 2, 3), (3, 3, 4)], 5, [0, 4]);
                case "path6":
                    return ([(0, 0, 1), (1, 1, 2), (2, 2, 3), (3, 3, 4), (4, 4, 5), (5, 5, 6)], 7, [0, 6, 3]);
                case "rect":
                    return ([(0, 0, 1), (1, 1, 2), (2, 2, 3), (3, 3, 0)], 4, [0, 2]);
                case "star":
                    return ([(0, 0, 1), (1, 0, 2), (2, 0, 3)], 4, [1, 2, 3]);
                case "star4":
                    return ([(0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 0, 4)], 5, [1, 2, 3, 4]);
                case "tree5":
                    return ([(0, 0, 1), (1, 1, 2), (2, 2, 3), (3, 1, 4), (4, 4, 5)], 6, [0, 3, 5]);
                case "spider":
                    {
                        int[][] legs = [[0, 1, 2, 3], [0, 4, 5, 6], [0, 7, 8, 9]];
                        var atoms = new List<(int, int, int)>();
                        foreach (var leg in legs)
                            for (var w = 0; w + 1 < leg.Length; w++)
                                atoms.Add((atoms.Count, leg[w], leg[w + 1]));

                        return (atoms.ToArray(), 10, [3, 6, 9]);
                    }
                default:
                    throw new ArgumentException("Unknown experiment " + name, nameof(name));
            }
        }

        /// <summary>
        /// The names of the experiments.
        /// </summary>
        public static readonly string[] Names = ["triangle", "path3", "path4", "path6", "rect", "star", "star4", "tree5", "spider"];

        /// <summary>
        /// Builds the database and the rule set of an experiment. The action records the output variables into
        /// <paramref name="log"/>, or passes them to <paramref name="action"/> when it is given.
        /// </summary>
        public static (Database Db, RuleSet Rules) Build(string name, PlanStrategy strategy, string mode, List<uint[]> log, bool noDecomposition = false,
            ExternalFunction? action = null)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            var ids = new List<TableId>();
            foreach (var (n, m) in Tables(name))
            {
                var table = db.AddTable(new SortedWritesTable(2, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
                DatabaseTestUtil.Stage(db, table, PlanTestDatabase.ExperimentRows(n, m).ToArray());
                ids.Add(table);
            }

            db.MergeAll();
            var recorder = db.AddExternalFunction(action ?? ExecutionTestUtil.Recorder(log));
            var (atoms, varCount, output) = Spec(name);
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            q.Strategy = strategy;
            q.NoDecomposition = noDecomposition;
            var vars = Enumerable.Range(0, varCount).Select(i => q.NewVariable("v" + i)).ToArray();
            foreach (var (table, a, b) in atoms)
                q.AddAtom(ids[table], [vars[a], vars[b]]);

            var rb = q.Build();
            rb.CallExternal(recorder, output.Select(i => QueryEntry.Var(vars[i])).ToArray());
            rb.Build(name);
            return (db, rsb.Build());
        }

        /// <summary>
        /// The distinct output tuples of an experiment, by brute force over the live rows.
        /// </summary>
        public static SortedSet<string> Reference(Database db, string name)
        {
            var (atoms, varCount, output) = Spec(name);
            var rows = Enumerable.Range(0, Tables(name).Length).Select(t => TableTestUtil.LiveRows(db.GetTable(new TableId(t)))).ToArray();
            var values = new uint[varCount];
            var bound = new bool[varCount];
            var result = new SortedSet<string>(StringComparer.Ordinal);
            Recurse(0);
            return result;

            void Recurse(int depth)
            {
                if (depth == atoms.Length)
                {
                    result.Add(string.Join(",", output.Select(i => values[i])));
                    return;
                }

                var (table, a, b) = atoms[depth];
                foreach (var row in rows[table])
                {
                    var newA = bound[a] == false;
                    if (newA == false && values[a] != row[0])
                        continue;

                    values[a] = row[0];
                    bound[a] = true;
                    var newB = bound[b] == false;
                    if (newB == false && values[b] != row[1])
                    {
                        if (newA)
                            bound[a] = false;

                        continue;
                    }

                    values[b] = row[1];
                    bound[b] = true;
                    Recurse(depth + 1);
                    if (newB)
                        bound[b] = false;
                    if (newA)
                        bound[a] = false;
                }
            }
        }

    }

}
