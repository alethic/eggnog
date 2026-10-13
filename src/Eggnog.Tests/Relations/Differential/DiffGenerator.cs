using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Generates differential programs from a seed (<see cref="DiffProgram"/>): random tables (arities, key counts,
    /// merge functions, rebuild columns, sharding, strata), and steps that load, remove, union, rebuild, churn, clear,
    /// warm indexes, clone the database, and run random rules (rule sets under every configuration, semi-naive variants,
    /// cached plans reused across mutations).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queries start from a join graph (<see cref="DiffShape"/>: paths, stars, cycles, trees, random hypergraphs) whose
    /// variables go into random columns of random tables, the union-find included; the other columns get fresh
    /// variables, variables shared with any other atom, a variable repeated within the atom, constants, or wildcards;
    /// timestamp columns mostly get wildcards. Atoms get random comparison and column-equality constraints, and on
    /// sorted tables semi-naive style ranges of the timestamp column.
    /// </para>
    /// <para>
    /// Some queries are projected onto a random subset of their bound variables (<see cref="DiffQuery.Projection"/>),
    /// and some run steps and cached loops get the dense lost-constraint shape (<see cref="DiffShape.Dense"/>), which is
    /// always projected, so that plans with unread variables and materialization lookups are compared too.
    /// </para>
    /// <para>
    /// Actions record every match (always), then may assert, look up, insert into deterministic tables (stamped with
    /// the epoch of the run), remove keys from any table, and union ids, in a random order in which lookups precede the
    /// instructions that read their results. They read only the recorded variables.
    /// </para>
    /// </remarks>
    static class DiffGenerator
    {

        /// <summary>
        /// Generates the program for <paramref name="seed"/> under <paramref name="profile"/>.
        /// </summary>
        public static DiffProgram Generate(int seed, DiffProfile profile)
        {
            var rng = new Random(seed);
            var mode = rng.Next(4) switch { 0 or 1 => "Serial", 2 => "Parallel1", _ => "Parallel4" };
            var domain = rng.Next(profile.MinDomain, profile.MaxDomain + 1);
            var count = rng.Next(profile.MinTables, profile.MaxTables + 1);
            var hasUnionFind = rng.NextDouble() < profile.UnionFindChance;
            var ufIndex = hasUnionFind ? rng.Next(count + 1) : -1;
            var tables = new List<DiffTable>();
            for (var i = 0; i < count + (hasUnionFind ? 1 : 0); i++)
                tables.Add(i == ufIndex ? DiffTable.UnionFind(i) : RandomTable(rng, i, ufIndex, profile.MaxDataArity, profile.MaxRowsPerLoad > ParallelHeuristics.TableOpThreshold));

            // Rules need somewhere to write.
            if (tables.Any(t => t.Deterministic) == false)
            {
                var victim = tables.First(t => t.IsUnionFind == false).Index;
                tables[victim] = SetTable(rng, victim);
            }

            // Now and then the tables of the lost-constraint shape, after the others.
            int[]? dense = null;
            if (profile.DenseChance > 0 && rng.NextDouble() < profile.DenseChance)
            {
                dense = [tables.Count, tables.Count + 1, tables.Count + 2];
                tables.AddRange(DenseTables(rng, tables.Count));
            }

            var program = new DiffProgram(seed, profile.Name, mode, domain, tables, Array.Empty<DiffStep>(), dense);
            var steps = new List<DiffStep>();

            // The dense tables' initial loads come on top, so that they do not crowd out the steps that use them.
            var stepCount = rng.Next(profile.MinSteps, profile.MaxSteps + 1) + (dense?.Length ?? 0);
            var writable = tables.Where(t => t.IsUnionFind == false).ToArray();

            // Start with every table loaded, and a few unions, so that queries have rows to match.
            foreach (var table in writable)
                steps.Add(Load(rng, program, profile, table));
            if (hasUnionFind)
            {
                steps.Add(Unions(rng, program, profile));
                if (program.RebuildTables.Length > 0 && rng.Next(2) == 0)
                    steps.Add(new DiffRebuildStep(8));
            }

            while (steps.Count < stepCount)
            {
                var step = RandomStep(rng, program, profile, writable, steps.Count);
                if (rng.NextDouble() < profile.PlantChance)
                    steps.AddRange(Plant(rng, program, step));

                steps.Add(step);
            }

            return program with { Steps = steps };
        }

        /// <summary>
        /// Generates a step. <paramref name="horizon"/> estimates the epoch the step will run at (each step advances it
        /// at least once), so that timestamp constants land among the timestamps the tables hold.
        /// </summary>
        static DiffStep RandomStep(Random rng, DiffProgram program, DiffProfile profile, DiffTable[] writable, int horizon)
        {
            var hasUnionFind = program.UnionFind >= 0;
            var rebuilds = program.RebuildTables.Length > 0;
            while (true)
            {
                var roll = rng.Next(100);
                switch (roll)
                {
                    case < 24:
                        return Load(rng, program, profile, Pick(rng, writable));
                    case < 30:
                        return Remove(rng, program, Pick(rng, writable));
                    case < 37 when hasUnionFind:
                        return Unions(rng, program, profile);
                    case < 45 when hasUnionFind && rebuilds:
                        return new DiffRebuildStep(8);
                    case < 49:
                        return new DiffChurnStep(Pick(rng, writable).Index, 1 + rng.Next(3));
                    case < 53:
                        return Warm(rng, Pick(rng, program.Tables.ToArray()));
                    case < 55:
                        return new DiffCloneStep();
                    case < 56:
                        return new DiffClearStep(Pick(rng, program.Tables.ToArray()).Index);
                    case < 80:
                        return Run(rng, program, profile, horizon);
                    case < 91:
                        return SemiNaive(rng, program, profile, horizon);
                    case >= 91:
                        return CachedLoop(rng, program, profile, writable, horizon);
                }
            }
        }

        /// <summary>
        /// Generates loads that plant a match of every query of <paramref name="step"/>: a random value per variable, and
        /// for each atom over a sorted-writes table the row those values, its constants and random wildcard cells make.
        /// Merge functions, constraints and timestamps can still defeat a plant; it only makes matches likely.
        /// </summary>
        static IEnumerable<DiffStep> Plant(Random rng, DiffProgram program, DiffStep step)
        {
            var queries = step switch
            {
                DiffRunStep run => run.Queries,
                DiffSemiNaiveStep semi => [semi.Query],
                DiffCachedLoopStep loop => [loop.Query],
                _ => Array.Empty<DiffQuery>(),
            };

            var rows = new SortedDictionary<int, List<uint[]>>();
            foreach (var query in queries)
            {
                var assignment = Enumerable.Range(0, query.VariableCount).Select(_ => Cell(rng, program)).ToArray();
                foreach (var atom in query.Atoms)
                {
                    var table = program.Tables[atom.Table];
                    if (table.IsUnionFind)
                        continue;

                    var row = new uint[table.DataArity];
                    for (var c = 0; c < row.Length; c++)
                    {
                        var e = atom.Entries[c];
                        row[c] = e.IsVariable == false ? e.Constant.Rep : e.IsWildcard ? Cell(rng, program) : assignment[e.Variable.Index];
                    }

                    if (rows.TryGetValue(table.Index, out var list) == false)
                        rows.Add(table.Index, list = new List<uint[]>());

                    list.Add(row);
                }
            }

            foreach (var (table, list) in rows)
                yield return new DiffLoadStep(table, list.ToArray());
        }

        /// <summary>
        /// Generates a step that unions random pairs of ids.
        /// </summary>
        static DiffUnionStep Unions(Random rng, DiffProgram program, DiffProfile profile) =>
            new(Enumerable.Range(0, 1 + rng.Next(profile.MaxUnionPairs)).Select(_ => new[] { Cell(rng, program), Cell(rng, program) }).ToArray());

        /// <summary>
        /// Generates a sorted-writes table: arity, timestamp column, key count, merge function, rebuild columns (only on
        /// deterministic tables, see <see cref="DiffTable.Deterministic"/>), sharding and declared dependencies.
        /// </summary>
        /// <remarks>
        /// When loads can exceed <see cref="ParallelHeuristics.TableOpThreshold"/> rows (<paramref name="bigLoads"/>), a
        /// <see cref="DiffMergeKind.KeepIncoming"/> table stays single-sharded: a sharded table merges such a load with
        /// the parallel insert, which folds each key's rows before merging the fold into the table, and
        /// <see cref="KeepIncomingMerge"/> is not associative (rows with the same value but other cells differ), so the
        /// result legitimately differs from the serial fold the model replays (see <see cref="MergeFunction"/>).
        /// </remarks>
        static DiffTable RandomTable(Random rng, int index, int ufIndex, int maxDataArity, bool bigLoads)
        {
            var dataArity = maxDataArity > 4 ? rng.Next(3, maxDataArity + 1) : rng.Next(100) switch { < 15 => 1, < 55 => 2, < 85 => 3, _ => 4 };
            var hasTs = rng.Next(100) < 60;
            var roll = rng.Next(100);
            var keyCount = roll < 5 ? 0 : roll < 35 || dataArity == 1 ? dataArity : rng.Next(1, dataArity);
            var arity = dataArity + (hasTs ? 1 : 0);
            var hasUnionFind = ufIndex >= 0;

            DiffMergeKind merge;
            var value = -1;
            if (keyCount == dataArity)
            {
                merge = rng.Next(2) == 0 ? DiffMergeKind.TakeNew : DiffMergeKind.KeepCurrent;
            }
            else
            {
                value = dataArity == keyCount + 1 ? keyCount : rng.Next(keyCount, dataArity);
                var deterministic = dataArity == keyCount + 1 && rng.Next(100) < 60;
                var options = deterministic
                    ? (hasUnionFind ? new[] { DiffMergeKind.Min, DiffMergeKind.Max, DiffMergeKind.UnionId, DiffMergeKind.UnionId } : new[] { DiffMergeKind.Min, DiffMergeKind.Max })
                    : (hasUnionFind
                        ? new[] { DiffMergeKind.TakeNew, DiffMergeKind.KeepCurrent, DiffMergeKind.KeepIncoming, DiffMergeKind.Min, DiffMergeKind.Max, DiffMergeKind.UnionId }
                        : new[] { DiffMergeKind.TakeNew, DiffMergeKind.KeepCurrent, DiffMergeKind.KeepIncoming, DiffMergeKind.Min, DiffMergeKind.Max });
                merge = options[rng.Next(options.Length)];
            }

            var probe = new DiffTable(index, keyCount, arity, hasTs, merge, value, false, [], false, [], []);
            var rebuild = Array.Empty<int>();
            if (hasUnionFind && probe.Deterministic && rng.Next(100) < 65)
            {
                var columns = Enumerable.Range(0, dataArity).Where(_ => rng.Next(2) == 0).ToList();
                if (merge == DiffMergeKind.UnionId && columns.Contains(value) == false && rng.Next(100) < 80)
                    columns.Add(value);
                if (columns.Count == 0)
                    columns.Add(rng.Next(dataArity));

                columns.Sort();
                rebuild = columns.ToArray();
            }

            var reads = new List<int>();
            var writes = new List<int>();
            if (merge == DiffMergeKind.UnionId)
            {
                if (ufIndex < index && rng.Next(2) == 0)
                    reads.Add(ufIndex);
                else
                    writes.Add(ufIndex);
            }

            if (index > 0 && rng.Next(100) < 15)
            {
                var dependency = rng.Next(index);
                if (reads.Contains(dependency) == false)
                    reads.Add(dependency);
            }

            var unionUsesTimestamp = rng.Next(2) == 0;
            var sharded = rng.Next(2) == 0 && (bigLoads == false || merge != DiffMergeKind.KeepIncoming);
            return new DiffTable(index, keyCount, arity, hasTs, merge, value, unionUsesTimestamp, rebuild, sharded, reads.ToArray(), writes.ToArray());
        }

        /// <summary>
        /// Generates a set table (every non-timestamp column a key), which is always deterministic.
        /// </summary>
        static DiffTable SetTable(Random rng, int index)
        {
            var dataArity = 1 + rng.Next(3);
            var hasTs = rng.Next(2) == 0;
            return new DiffTable(index, dataArity, dataArity + (hasTs ? 1 : 0), hasTs, rng.Next(2) == 0 ? DiffMergeKind.TakeNew : DiffMergeKind.KeepCurrent, -1, false, [], rng.Next(2) == 0, [], []);
        }

        /// <summary>
        /// Generates a load of random rows into <paramref name="table"/>.
        /// </summary>
        static DiffLoadStep Load(Random rng, DiffProgram program, DiffProfile profile, DiffTable table)
        {
            var rows = new uint[rng.Next(profile.MinRowsPerLoad, profile.MaxRowsPerLoad + 1)][];
            for (var i = 0; i < rows.Length; i++)
                rows[i] = Enumerable.Range(0, table.DataArity).Select(_ => Cell(rng, program)).ToArray();

            return new DiffLoadStep(table.Index, rows);
        }

        /// <summary>
        /// Generates removals of random keys, present or not, from <paramref name="table"/>.
        /// </summary>
        static DiffRemoveStep Remove(Random rng, DiffProgram program, DiffTable table)
        {
            var keys = new uint[1 + rng.Next(3)][];
            for (var i = 0; i < keys.Length; i++)
                keys[i] = Enumerable.Range(0, table.KeyCount).Select(_ => Cell(rng, program)).ToArray();

            return new DiffRemoveStep(table.Index, keys);
        }

        /// <summary>
        /// Generates the building of one to three cached indexes of <paramref name="table"/>.
        /// </summary>
        static DiffWarmStep Warm(Random rng, DiffTable table)
        {
            var lists = new int[1 + rng.Next(3)][];
            for (var i = 0; i < lists.Length; i++)
            {
                var first = rng.Next(table.Arity);
                if (table.Arity == 1 || rng.Next(100) < 60)
                {
                    lists[i] = [first];
                }
                else
                {
                    var second = rng.Next(table.Arity - 1);
                    lists[i] = [first, second >= first ? second + 1 : second];
                }
            }

            return new DiffWarmStep(table.Index, lists);
        }

        /// <summary>
        /// Generates a rule-set run: up to <see cref="DiffProfile.MaxQueriesPerRun"/> queries, each under
        /// <see cref="DiffProfile.ConfigsPerQuery"/> configurations (one in five from a cached plan).
        /// </summary>
        static DiffRunStep Run(Random rng, DiffProgram program, DiffProfile profile, int horizon)
        {
            var queries = Enumerable.Range(0, 1 + rng.Next(profile.MaxQueriesPerRun)).Select(_ => Query(rng, program, profile, preferSorted: false, horizon, allowDense: true)).ToArray();
            var configs = DiffConfig.All.OrderBy(_ => rng.Next()).Take(profile.ConfigsPerQuery).Select(c => c with { Cached = rng.Next(100) < 20 }).ToArray();
            return new DiffRunStep(queries, configs, 1 + rng.Next(profile.MaxIterations));
        }

        /// <summary>
        /// Generates the semi-naive variants of a query over sorted tables, under two configurations, with a pinned
        /// equality now and then.
        /// </summary>
        static DiffSemiNaiveStep SemiNaive(Random rng, DiffProgram program, DiffProfile profile, int horizon)
        {
            var configs = DiffConfig.All.OrderBy(_ => rng.Next()).Take(2).Select(c => c with { Cached = rng.Next(2) == 0 }).ToArray();
            var query = Query(rng, program, profile, preferSorted: true, horizon);
            var pinned = new List<(int, Constraint)>();
            if (rng.Next(100) < 25)
            {
                var atom = rng.Next(query.Atoms.Length);
                var table = program.Tables[query.Atoms[atom].Table];
                // The union-find's canonical column is computed on every read, so an equality on it is never fast: the
                // pin goes on the child column.
                var column = table.IsUnionFind ? 0 : rng.Next(table.DataArity);
                pinned.Add((atom, Constraint.EqConst(new ColumnId(column), new Value(Cell(rng, program)))));
            }

            return new DiffSemiNaiveStep(query, rng.Next(4), configs, pinned.ToArray());
        }

        /// <summary>
        /// Generates a query whose cached plans are reused over two to four rounds with mutations in between.
        /// </summary>
        static DiffCachedLoopStep CachedLoop(Random rng, DiffProgram program, DiffProfile profile, DiffTable[] writable, int horizon)
        {
            var query = Query(rng, program, profile, preferSorted: true, horizon, allowDense: true);
            var configs = DiffConfig.All.OrderBy(_ => rng.Next()).Take(1 + rng.Next(3)).Select(c => c with { Cached = true }).ToArray();
            var rounds = 2 + rng.Next(3);
            var focus = Enumerable.Range(0, rounds).Select(r => r > 0 && rng.Next(100) < 60 ? rng.Next(query.Atoms.Length) : -1).ToArray();
            var between = new DiffStep[rounds - 1];
            for (var i = 0; i < between.Length; i++)
            {
                between[i] = rng.Next(100) switch
                {
                    < 35 => Load(rng, program, profile, Pick(rng, writable)),
                    < 45 => Remove(rng, program, Pick(rng, writable)),
                    < 55 when program.UnionFind >= 0 => Unions(rng, program, profile),
                    < 65 when program.UnionFind >= 0 && program.RebuildTables.Length > 0 => new DiffRebuildStep(8),
                    < 80 => new DiffChurnStep(Pick(rng, writable).Index, 1 + rng.Next(3)),
                    < 85 => new DiffClearStep(Pick(rng, writable).Index),
                    < 92 => Warm(rng, Pick(rng, program.Tables.ToArray())),
                    _ => new DiffCloneStep(),
                };
            }

            return new DiffCachedLoopStep(query, configs, focus, between);
        }

        /// <summary>
        /// Generates a query: a join graph, its variables placed into random columns of random tables, the other columns
        /// filled, constraints added, now and then a projection, and an action. With <paramref name="allowDense"/>, a
        /// program with dense tables gets a query of the lost-constraint shape (<see cref="DenseQuery"/>) half of the
        /// time instead.
        /// </summary>
        static DiffQuery Query(Random rng, DiffProgram program, DiffProfile profile, bool preferSorted, int horizon, bool allowDense = false)
        {
            if (allowDense && program.DenseTables is not null && rng.Next(2) == 0)
                return DenseQuery(rng, program);

            // Biased towards fewer atoms, which match more often; the most still come up regularly.
            var n = Math.Min(profile.MinAtoms + Math.Min(rng.Next(profile.MaxAtoms - profile.MinAtoms + 1), rng.Next(profile.MaxAtoms - profile.MinAtoms + 2)), profile.MaxAtoms);
            var shape = (DiffShape)rng.Next(5);
            var joins = new List<int>[n];
            int variables;
            switch (shape)
            {
                case DiffShape.Path:
                    for (var i = 0; i < n; i++)
                        joins[i] = [i, i + 1];
                    variables = n + 1;
                    break;
                case DiffShape.Star:
                    for (var i = 0; i < n; i++)
                        joins[i] = [0, i + 1];
                    variables = n + 1;
                    break;
                case DiffShape.Cycle:
                    for (var i = 0; i < n; i++)
                        joins[i] = [i, (i + 1) % n];
                    variables = n;
                    break;
                case DiffShape.Tree:
                    for (var i = 0; i < n; i++)
                        joins[i] = [rng.Next(i + 1), i + 1];
                    variables = n + 1;
                    break;
                default:
                    var pool = 1 + rng.Next(n + 1);
                    for (var i = 0; i < n; i++)
                        joins[i] = Enumerable.Range(0, 1 + rng.Next(3)).Select(_ => rng.Next(pool)).Distinct().ToList();
                    variables = pool;
                    break;
            }

            // Selective features (constants, repeated variables, constraints) are budgeted per query rather than drawn
            // per atom, so that long queries still match; one query in ten gets a double budget.
            var rich = rng.Next(10) == 0 ? 2 : 1;
            var constants = rich * rng.Next(100) switch { < 50 => 0, < 85 => 1, _ => 2 };
            var repeats = rich * (rng.Next(100) < 30 ? 1 : 0);
            var sortedTables = program.Tables.Where(t => t.IsSorted).ToArray();
            var atoms = new DiffAtom[n];
            for (var i = 0; i < n; i++)
            {
                var table = preferSorted && sortedTables.Length > 0 && rng.Next(100) < 80 ? Pick(rng, sortedTables) : Pick(rng, program.Tables.ToArray());
                atoms[i] = Atom(rng, program, table, joins[i], horizon, ref variables, ref constants, ref repeats);
            }

            // Now and then an atom repeated verbatim (a self-join on the same row), or a query of existence checks only.
            for (var i = 1; i < n; i++)
                if (rng.Next(100) < 5)
                    atoms[i] = atoms[rng.Next(i)];

            var allGround = rng.Next(100) < 3;

            // Now and then an atom without variables: only constants and wildcards, an existence check.
            for (var i = 0; i < n; i++)
            {
                if (allGround == false && rng.Next(100) >= 6)
                    continue;

                var table = program.Tables[atoms[i].Table];
                var ground = Enumerable.Range(0, table.Arity)
                    .Select(c => c != table.TimestampColumn && rng.Next(100) < 40 ? QueryEntry.Const(new Value(Cell(rng, program))) : QueryEntry.Wildcard())
                    .ToArray();
                atoms[i] = atoms[i] with { Entries = ground };
            }

            for (var k = rich * rng.Next(100) switch { < 60 => 0, < 92 => 1, _ => 2 }; k > 0; k--)
            {
                var i = rng.Next(n);
                atoms[i] = atoms[i].WithConstraints(RandomConstraint(rng, program, program.Tables[atoms[i].Table], horizon));
            }

            // Semi-naive style ranges of the timestamp column, which become header constraints.
            for (var i = 0; i < n; i++)
            {
                var table = program.Tables[atoms[i].Table];
                if (table.IsSorted == false || rng.Next(100) >= 12)
                    continue;

                var ts = new ColumnId(table.TimestampColumn);
                var low = (uint)rng.Next(horizon + 1);
                if (rng.Next(2) == 0)
                    atoms[i] = atoms[i].WithConstraints(Constraint.GeConst(ts, new Value(low)));
                if (rng.Next(2) == 0)
                    atoms[i] = atoms[i].WithConstraints(Constraint.LtConst(ts, new Value(low + (uint)rng.Next(1, horizon + 2))));
            }

            var projection = rng.NextDouble() < profile.ProjectChance ? Projection(rng, atoms, rng.Next(2) == 0 ? 30 : 60) : null;
            var query = new DiffQuery(shape, variables, atoms, [], projection);
            return query with { Actions = Actions(rng, program, query) };
        }

        /// <summary>
        /// The lost-constraint query of PlannerDecompositionTests, which egglog's planner answers wrongly and this
        /// planner answers with a materialization lookup in its result block, for almost any data and almost any choice
        /// of read variables: per atom, the role of its table (0: four columns, 1: one, 2: three;
        /// <see cref="DiffProgram.DenseTables"/>), its entries (a variable <c>0..8</c>, <see cref="TemplateWildcard"/>
        /// or <see cref="TemplateConstant"/>) and the constraint it has there, if any.
        /// </summary>
        static readonly (int Role, int[] Entries, Constraint? Constraint)[] LostConstraintTemplate =
        [
            (0, [4, 4, 2, 3], null),
            (2, [3, 1, TemplateConstant], Constraint.Eq(new ColumnId(0), new ColumnId(1))),
            (1, [5], Constraint.Eq(new ColumnId(0), new ColumnId(0))),
            (2, [TemplateWildcard, TemplateConstant, 7], null),
            (0, [2, 7, 7, 5], Constraint.EqConst(new ColumnId(3), new Value(1))),
            (1, [4], null),
            (0, [8, 5, 6, 6], null),
            (0, [TemplateWildcard, 1, 2, 0], null),
            (2, [0, 5, 8], Constraint.GeConst(new ColumnId(2), new Value(1))),
        ];

        /// <summary>
        /// A wildcard entry of <see cref="LostConstraintTemplate"/>.
        /// </summary>
        const int TemplateWildcard = -1;

        /// <summary>
        /// A constant entry of <see cref="LostConstraintTemplate"/>; the generator draws its value.
        /// </summary>
        const int TemplateConstant = -2;

        /// <summary>
        /// The variables of <see cref="LostConstraintTemplate"/>.
        /// </summary>
        const int TemplateVariables = 9;

        /// <summary>
        /// Generates a query of the lost-constraint shape (<see cref="DiffShape.Dense"/>) over the program's dense tables:
        /// <see cref="LostConstraintTemplate"/> with its variables renumbered and its atoms shuffled at random, its
        /// constants drawn from the domain, each of its constraints kept with even odds, up to two random entries changed
        /// and now and then an atom dropped or one more atom added (eight to ten atoms), timestamp columns mostly
        /// wildcards, and a projection onto two to six of its variables. Renumbering and the changes keep the variety up;
        /// about one decomposed plan in ten still needs a materialization lookup.
        /// </summary>
        static DiffQuery DenseQuery(Random rng, DiffProgram program)
        {
            var roles = program.DenseTables!;
            var rename = Enumerable.Range(0, TemplateVariables).OrderBy(_ => rng.Next()).ToArray();
            var template = LostConstraintTemplate.Select(t => (t.Role, Entries: (int[])t.Entries.Clone(), Constraint: rng.Next(2) == 0 ? t.Constraint : null)).ToList();
            switch (rng.Next(100))
            {
                case < 15:
                    template.RemoveAt(rng.Next(template.Count));
                    break;
                case < 30:
                    var copy = template[rng.Next(template.Count)];
                    template.Add((copy.Role, (int[])copy.Entries.Clone(), null));
                    template[^1].Entries[rng.Next(copy.Entries.Length)] = rng.Next(TemplateVariables);
                    break;
            }

            for (var k = rng.Next(100) switch { < 50 => 0, < 80 => 1, _ => 2 }; k > 0; k--)
            {
                var entries = template[rng.Next(template.Count)].Entries;
                entries[rng.Next(entries.Length)] = rng.Next(10) switch { < 7 => rng.Next(TemplateVariables), < 9 => TemplateWildcard, _ => TemplateConstant };
            }

            var atoms = new List<DiffAtom>();
            foreach (var (role, entries, constraint) in template.OrderBy(_ => rng.Next()))
            {
                var table = program.Tables[roles[role]];
                var row = new QueryEntry[table.Arity];
                for (var c = 0; c < table.Arity; c++)
                {
                    if (c == table.TimestampColumn)
                    {
                        row[c] = rng.Next(100) < 85 ? QueryEntry.Wildcard() : QueryEntry.Var(new Variable(rng.Next(TemplateVariables)));
                        continue;
                    }

                    var e = entries[c];
                    row[c] = e switch
                    {
                        TemplateWildcard => QueryEntry.Wildcard(),
                        TemplateConstant => QueryEntry.Const(new Value(Cell(rng, program))),
                        _ => QueryEntry.Var(new Variable(rename[e])),
                    };
                }

                atoms.Add(new DiffAtom(table.Index, row, constraint is { } x ? [x] : []));
            }

            var bound = new DiffQuery(DiffShape.Dense, TemplateVariables, atoms.ToArray(), []).BoundVariables();
            var read = bound.OrderBy(_ => rng.Next()).Take(Math.Min(bound.Length, rng.Next(2, 7))).OrderBy(v => v).ToArray();
            var query = new DiffQuery(DiffShape.Dense, TemplateVariables, atoms.ToArray(), [], read);
            return query with { Actions = Actions(rng, program, query) };
        }

        /// <summary>
        /// Generates the tables the lost-constraint shape is written over (<see cref="DiffProgram.DenseTables"/>): set
        /// tables of four, one and three columns, with or without a timestamp, sharded or not.
        /// </summary>
        static DiffTable[] DenseTables(Random rng, int first) =>
            new[] { 4, 1, 3 }.Select((dataArity, i) =>
            {
                var hasTs = rng.Next(2) == 0;
                var merge = rng.Next(2) == 0 ? DiffMergeKind.TakeNew : DiffMergeKind.KeepCurrent;
                return new DiffTable(first + i, dataArity, dataArity + (hasTs ? 1 : 0), hasTs, merge, -1, false, [], rng.Next(2) == 0, [], []);
            }).ToArray();

        /// <summary>
        /// Generates a projection of the variables <paramref name="atoms"/> bind: each kept with probability
        /// <paramref name="percent"/> in a hundred, so that it may keep all of them or none.
        /// </summary>
        static int[] Projection(Random rng, DiffAtom[] atoms, int percent)
        {
            var bound = new SortedSet<int>();
            foreach (var atom in atoms)
                foreach (var e in atom.Entries)
                    if (e.IsVariable && e.IsWildcard == false)
                        bound.Add(e.Variable.Index);

            return bound.Where(_ => rng.Next(100) < percent).ToArray();
        }

        /// <summary>
        /// Generates one atom over <paramref name="table"/> holding the join variables <paramref name="joins"/>; the other
        /// columns get fresh variables, wildcards, shared variables, and, while the query's budgets last, constants and
        /// repeated variables.
        /// </summary>
        static DiffAtom Atom(Random rng, DiffProgram program, DiffTable table, List<int> joins, int horizon, ref int variables, ref int constants, ref int repeats)
        {
            var entries = new QueryEntry?[table.Arity];

            // Join variables go into the data columns first, the timestamp column only when they do not fit.
            var columns = Enumerable.Range(0, table.DataArity).OrderBy(_ => rng.Next()).ToList();
            if (table.IsSorted)
                columns.Add(table.TimestampColumn);

            for (var j = 0; j < joins.Count && j < columns.Count; j++)
                entries[columns[j]] = QueryEntry.Var(new Variable(joins[j]));

            for (var c = 0; c < table.Arity; c++)
            {
                if (entries[c] is not null)
                    continue;

                var inAtom = entries.Where(e => e is { IsVariable: true, IsWildcard: false }).Select(e => e!.Value.Variable).ToList();
                var roll = rng.Next(100);
                if (c == table.TimestampColumn)
                {
                    entries[c] = roll switch
                    {
                        < 60 => QueryEntry.Wildcard(),
                        < 90 => QueryEntry.Var(new Variable(variables++)),
                        < 95 when variables > 0 => QueryEntry.Var(new Variable(rng.Next(variables))),
                        _ => QueryEntry.Const(new Value((uint)rng.Next(horizon + 1))),
                    };
                }
                else if (roll < 8 && constants > 0)
                {
                    constants--;
                    entries[c] = QueryEntry.Const(new Value(Cell(rng, program)));
                }
                else if (roll < 16 && repeats > 0 && inAtom.Count > 0)
                {
                    repeats--;
                    entries[c] = QueryEntry.Var(inAtom[rng.Next(inAtom.Count)]);
                }
                else
                {
                    // A variable shared with other atoms, but not repeated within this one (repeats are budgeted).
                    var others = Enumerable.Range(0, variables).Where(v => inAtom.Contains(new Variable(v)) == false).ToList();
                    entries[c] = roll switch
                    {
                        < 55 => QueryEntry.Var(new Variable(variables++)),
                        < 85 => QueryEntry.Wildcard(),
                        _ when others.Count > 0 => QueryEntry.Var(new Variable(others[rng.Next(others.Count)])),
                        _ => QueryEntry.Var(new Variable(variables++)),
                    };
                }
            }

            return new DiffAtom(table.Index, entries.Select(e => e!.Value).ToArray(), []);
        }

        /// <summary>
        /// Generates a comparison of a column with a constant, or an equality of two columns.
        /// </summary>
        static Constraint RandomConstraint(Random rng, DiffProgram program, DiffTable table, int horizon)
        {
            // Timestamp columns get ranges around the estimated epoch, which usually select something.
            if (table.IsSorted && rng.Next(100) < 25)
            {
                var ts = new ColumnId(table.TimestampColumn);
                return rng.Next(5) switch
                {
                    0 => Constraint.GeConst(ts, new Value((uint)rng.Next(horizon / 2 + 1))),
                    1 => Constraint.GtConst(ts, new Value((uint)rng.Next(horizon / 2 + 1))),
                    2 => Constraint.LtConst(ts, new Value((uint)rng.Next(horizon / 2, 2 * horizon + 2))),
                    3 => Constraint.LeConst(ts, new Value((uint)rng.Next(horizon / 2, 2 * horizon + 2))),
                    _ => Constraint.EqConst(ts, new Value((uint)rng.Next(horizon + 1))),
                };
            }

            var c = new ColumnId(rng.Next(table.DataArity));
            var value = new Value(Cell(rng, program));
            return rng.Next(7) switch
            {
                0 => Constraint.EqConst(c, value),
                1 => Constraint.LtConst(c, value),
                2 => Constraint.GtConst(c, value),
                3 => Constraint.LeConst(c, value),
                4 => Constraint.GeConst(c, value),
                _ => Constraint.Eq(c, new ColumnId(rng.Next(table.Arity))),
            };
        }

        /// <summary>
        /// Generates the instructions after the recorder call.
        /// </summary>
        static DiffAction[] Actions(Random rng, DiffProgram program, DiffQuery query)
        {
            var bound = query.RecordedVariables();
            var targets = program.Tables.Where(t => t.Deterministic).ToArray();
            var readable = program.Tables.Where(t => t.IsUnionFind == false).ToArray();
            var lookups = new List<int>();
            var actions = new List<DiffAction>();
            var count = rng.Next(100) switch { < 10 => 0, < 50 => 1, < 80 => 2, _ => 3 };
            for (var i = 0; i < count; i++)
            {
                var roll = rng.Next(100);
                if (roll < 55 && targets.Length > 0)
                {
                    var table = Pick(rng, targets);
                    var row = Enumerable.Range(0, table.Arity).Select(c => c == table.TimestampColumn ? DiffAction.Epoch : Operand(rng, program, bound, lookups)).ToArray();
                    actions.Add(new DiffAction(DiffActionKind.Insert, table.Index, row, -1, -1));
                }
                else if (roll < 65 && program.UnionFind >= 0)
                {
                    actions.Add(new DiffAction(DiffActionKind.Insert, program.UnionFind, [Operand(rng, program, bound, lookups), Operand(rng, program, bound, lookups), DiffAction.Epoch], -1, -1));
                }
                else if (roll < 75)
                {
                    var table = Pick(rng, readable);
                    var key = Enumerable.Range(0, table.KeyCount).Select(_ => Operand(rng, program, bound, lookups)).ToArray();
                    actions.Add(new DiffAction(DiffActionKind.Remove, table.Index, key, -1, -1));
                }
                else if (roll < 88)
                {
                    var table = Pick(rng, readable);
                    var key = Enumerable.Range(0, table.KeyCount).Select(_ => Operand(rng, program, bound, lookups)).ToArray();
                    var result = query.VariableCount + lookups.Count;
                    lookups.Add(result);
                    actions.Add(new DiffAction(DiffActionKind.Lookup, table.Index, key, rng.Next(table.Arity), result));
                }
                else
                {
                    var kind = rng.Next(2) == 0 ? DiffActionKind.AssertEq : DiffActionKind.AssertNe;
                    actions.Add(new DiffAction(kind, -1, [Operand(rng, program, bound, lookups), Operand(rng, program, bound, lookups)], -1, -1));
                }
            }

            return actions.ToArray();
        }

        /// <summary>
        /// Generates an action operand: a lookup result, a recorded variable or a constant.
        /// </summary>
        static QueryEntry Operand(Random rng, DiffProgram program, int[] bound, List<int> lookups)
        {
            var roll = rng.Next(100);
            if (roll < 20 && lookups.Count > 0)
                return QueryEntry.Var(new Variable(lookups[rng.Next(lookups.Count)]));
            if (roll < 85 && bound.Length > 0)
                return QueryEntry.Var(new Variable(bound[rng.Next(bound.Length)]));

            return QueryEntry.Const(new Value(Cell(rng, program)));
        }

        static uint Cell(Random rng, DiffProgram program) => (uint)rng.Next(program.Domain);

        static T Pick<T>(Random rng, T[] items) => items[rng.Next(items.Length)];

    }

}
