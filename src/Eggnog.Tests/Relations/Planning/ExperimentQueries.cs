using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The queries of the planner experiments (the scratchpad's plan_dump.rs, plan_dump2.rs and plan_dump3.rs, run against
    /// egglog at HEAD): each table is built from <see cref="PlanTestDatabase.ExperimentRows"/>, a variable entry is its
    /// index, a constant entry is the value 3, and the action reads the listed variables.
    /// </summary>
    static class ExperimentQueries
    {

        static readonly (int N, int M)[] Sizes4 = [(100, 30), (10, 5), (50, 20), (20, 10)];

        public static readonly string[] Names =
        [
            "triangle", "path3", "path4", "path6", "rect", "star", "star4", "spider", "tree5", "const_repeat", "join2_unused", "allconst",
        ];

        public static (PlanTestDatabase Db, Query Query) Build(string name, PlanStrategy strategy, bool noDecomposition = false)
        {
            var (tables, atoms, varCount, rhs) = Spec(name);
            var db = new PlanTestDatabase();
            var ids = new List<TableId>();
            foreach (var (n, m) in tables)
                ids.Add(db.AddTable(2, 2, PlanTestDatabase.ExperimentRows(n, m)));

            // The output table of the experiments; atoms never name it.
            db.AddTable(2, 2, []);

            var qb = new QueryBuilder(db) { Strategy = strategy, NoDecomposition = noDecomposition };
            var vars = new Variable[varCount];
            for (var i = 0; i < varCount; i++)
                vars[i] = qb.NewVariable("v" + i);

            foreach (var (table, entries, constraints) in atoms)
            {
                var es = new QueryEntry[entries.Length];
                for (var i = 0; i < entries.Length; i++)
                    es[i] = entries[i] is int v ? QueryEntry.Var(vars[v]) : QueryEntry.Const(new Value(3));

                qb.AddAtom(ids[table], es, constraints);
            }

            foreach (var v in rhs)
                qb.MarkUsed(vars[v]);

            return (db, qb.Build(new ActionId(0)));
        }

        static ((int N, int M)[] Tables, (int Table, int?[] Entries, Constraint[] Constraints)[] Atoms, int VarCount, int[] Rhs) Spec(string name)
        {
            var gt = Constraint.GtConst(new ColumnId(1), new Value(2));
            switch (name)
            {
                case "triangle":
                    return (Sizes4[..3], [(0, [0, 1], []), (1, [1, 2], []), (2, [2, 0], [])], 3, [0, 1, 2]);
                case "path3":
                    return (Sizes4[..3], [(0, [0, 1], []), (1, [1, 2], []), (2, [2, 3], [])], 4, [0, 3]);
                case "path4":
                    return (Sizes4, [(0, [0, 1], []), (1, [1, 2], []), (2, [2, 3], []), (3, [3, 4], [])], 5, [0, 4]);
                case "path6":
                    return ([(100, 30), (10, 5), (50, 20), (20, 10), (70, 9), (40, 13)],
                        [(0, [0, 1], []), (1, [1, 2], []), (2, [2, 3], []), (3, [3, 4], []), (4, [4, 5], []), (5, [5, 6], [])], 7, [0, 6, 3]);
                case "rect":
                    return (Sizes4, [(0, [0, 1], []), (1, [1, 2], []), (2, [2, 3], []), (3, [3, 0], [])], 4, [0, 2]);
                case "star":
                    return (Sizes4[..3], [(0, [0, 1], []), (1, [0, 2], []), (2, [0, 3], [])], 4, [1, 2, 3]);
                case "star4":
                    return (Sizes4, [(0, [0, 1], []), (1, [0, 2], []), (2, [0, 3], []), (3, [0, 4], [])], 5, [1, 2, 3, 4]);
                case "spider":
                    {
                        (int, int)[] sizes = [(100, 30), (10, 5), (50, 20), (20, 10), (70, 9), (40, 13), (30, 7), (60, 11), (25, 6)];
                        int[][] legs = [[0, 1, 2, 3], [0, 4, 5, 6], [0, 7, 8, 9]];
                        var atoms = new List<(int, int?[], Constraint[])>();
                        foreach (var leg in legs)
                            for (var w = 0; w + 1 < leg.Length; w++)
                                atoms.Add((atoms.Count, [leg[w], leg[w + 1]], []));

                        return (sizes, atoms.ToArray(), 10, [3, 6, 9]);
                    }
                case "tree5":
                    return ([(100, 30), (10, 5), (50, 20), (20, 10), (70, 9)],
                        [(0, [0, 1], []), (1, [1, 2], []), (2, [2, 3], []), (3, [1, 4], []), (4, [4, 5], [])], 6, [0, 3, 5]);
                case "const_repeat":
                    return (Sizes4[..3], [(0, [0, null], []), (1, [0, 0], []), (2, [0, 1], [gt])], 2, [1]);
                case "join2_unused":
                    return (Sizes4[..2], [(0, [0, 1], []), (1, [1, 2], [])], 3, [0]);
                case "allconst":
                    return (Sizes4[..2], [(0, [null, null], []), (1, [0, 1], [])], 2, [0, 1]);
                case "path4_nodecomp":
                    return Spec("path4");
                default:
                    throw new ArgumentException("Unknown experiment " + name);
            }
        }

    }

}
