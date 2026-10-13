using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The semi-naive variants of a <see cref="DiffSemiNaiveStep"/>, run over a layered copy of the engine's current
    /// tables instead of the tables themselves, and checked against the same oracle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The copy (<see cref="SyntheticLayers"/>): two to four levels of the program's tables. Every live row of a
    /// sorted-writes table goes to one level at random, keeping its timestamp, so each level's time column holds rows of
    /// any epoch and every variant's time range is one binary search per layer. Some keys also get a decoy, a different
    /// row with the same key in a lower level, which the view hides, as a context's view hides an ancestor's row that a
    /// deeper level overrides (the topmost layer holding a key decides). A union-find stays one layer
    /// (<see cref="TableView.ForTable"/>). So the view shows exactly the engine's live rows, which the runner compares
    /// with the model after every step, and every variant's distinct matches over the copy must be the oracle's, as in
    /// the flat run: the layered executor finds the distinct matches a naive join over the visible rows finds, and the
    /// variants' time ranges, split per layer, still partition the view, since hidden rows fall on neither side.
    /// </para>
    /// <para>
    /// The rules record their matches and nothing else, so the step changes neither the engine nor the model and does not
    /// advance the epoch.
    /// </para>
    /// </remarks>
    sealed class DiffLayeredSemiNaiveStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="semiNaive">The step whose variants run over the copy.</param>
        /// <param name="seed">The seed of the copy's layering.</param>
        public DiffLayeredSemiNaiveStep(DiffSemiNaiveStep semiNaive, int seed)
        {
            SemiNaive = semiNaive;
            Seed = seed;
        }

        /// <summary>
        /// Gets the step whose variants run over the copy.
        /// </summary>
        public DiffSemiNaiveStep SemiNaive { get; }

        /// <summary>
        /// Gets the seed of the copy's layering.
        /// </summary>
        public int Seed { get; }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            var (variants, expected, _, boundary) = SemiNaive.Split(runner);
            if (expected.Any(e => e is null))
                return;

            var (db, hidden) = Copy(runner, new Random(Seed));
            runner.Stats.LayeredHiddenRows += hidden;
            var recorder = new DiffRecorder();
            var function = db.AddExternalFunction(recorder.Function);
            var rsb = db.NewRuleSet();
            var rules = new List<(int Variant, DiffConfig Config, uint Tag, RuleId? Rule)>();
            var tag = 0u;
            foreach (var config in SemiNaive.Configs)
            {
                for (var v = 0; v < variants.Count; v++)
                {
                    rules.Add((v, config, tag, AddRule(db, rsb, SemiNaive.Query, config, tag, function, variants[v])));
                    tag++;
                }
            }

            using var rs = rsb.Build();
            db.RunRuleSet(rs);
            foreach (var (v, config, t, rule) in rules)
            {
                var label = $"layered semi-naive variant {v} at boundary {boundary} ({string.Join(", ", variants[v].Select(x => $"atom {x.Atom}: {x.Constraint}"))}) under {config}";
                runner.Stats.LayeredVariants++;
                var want = expected[v]!;
                if (rule is null)
                {
                    if (want.Count > 0)
                        runner.Fail($"{label}: the variant was skipped as empty over the layered copy, but the oracle has {want.Count} matches");

                    continue;
                }

                var got = recorder.Matches(t).ToHashSet(StringComparer.Ordinal);
                if (got.Count > 0)
                    runner.Stats.LayeredVariantsWithMatches++;

                if (got.SetEquals(want.Keys) == false)
                {
                    var missing = want.Keys.Where(k => got.Contains(k) == false).Take(5);
                    var extra = got.Where(k => want.ContainsKey(k) == false).Take(5);
                    runner.Fail($"{label}: the layered run found {got.Count} distinct matches, the oracle {want.Count}; missing [{string.Join(" ", missing)}], extra [{string.Join(" ", extra)}]{Environment.NewLine}{rs[rule.Value].Template}");
                }
            }
        }

        /// <inheritdoc />
        public override string Format(DiffProgram program) => $"layered(seed={Seed}) {SemiNaive.Format(program)}";

        /// <inheritdoc />
        public override IEnumerable<DiffStep> Simplify(DiffProgram program)
        {
            foreach (var simpler in SemiNaive.Simplify(program))
                if (simpler is DiffSemiNaiveStep semi)
                    yield return new DiffLayeredSemiNaiveStep(semi, Seed);
        }

        /// <summary>
        /// Builds the layered copy of the runner's tables: a database per level, the first of which the rules run in.
        /// </summary>
        /// <returns>The database the rules run in, and the number of hidden decoys.</returns>
        static (Database Db, long Hidden) Copy(DiffRunner runner, Random random)
        {
            var program = runner.Program;
            var depth = 1 + random.Next(3);
            var ufIndex = program.UnionFind;
            var uf = ufIndex >= 0 ? new TableId(ufIndex) : default;
            var levels = new Database[depth + 1];
            for (var d = 0; d <= depth; d++)
            {
                levels[d] = d == 0 ? ExecutionTestUtil.NewDatabase(program.Mode) : new Database(ParallelHeuristics.Serial);
                foreach (var t in program.Tables)
                    levels[d].AddTable(t.Create(uf), name: t.Name);
            }

            var layers = new SyntheticLayers(levels);
            long hiddenCount = 0;
            foreach (var t in program.Tables)
            {
                var id = new TableId(t.Index);
                var source = runner.Db.GetTable(runner.Id(t.Index));
                if (t.IsUnionFind || source is not SortedWritesTable)
                {
                    layers.Use(id, TableView.ForTable(id, runner.Db.GetTableInfo(runner.Id(t.Index)), false));
                    continue;
                }

                // Every live row to one level; a decoy of some keys below the row's level, hidden by the view.
                var perLevel = Enumerable.Range(0, depth + 1).Select(_ => new List<(uint[] Row, bool Decoy)>()).ToArray();
                foreach (var row in TableTestUtil.LiveRows(source))
                {
                    var level = random.Next(depth + 1);
                    perLevel[level].Add((row, false));
                    if (level > 0 && random.Next(3) == 0)
                    {
                        var decoy = (uint[])row.Clone();
                        for (var c = t.KeyCount; c < t.Arity; c++)
                            if (c != t.TimestampColumn)
                                decoy[c] = (uint)random.Next(program.Domain + 2);

                        perLevel[random.Next(level)].Add((decoy, true));
                    }
                }

                var hidden = new List<int>?[depth + 1];
                for (var d = 0; d <= depth; d++)
                {
                    var rows = perLevel[d];
                    if (t.IsSorted)
                        rows.Sort((a, b) => a.Row[t.TimestampColumn].CompareTo(b.Row[t.TimestampColumn]));

                    var start = 0;
                    while (start < rows.Count)
                    {
                        var end = start + 1;
                        while (end < rows.Count && (t.IsSorted == false || rows[end].Row[t.TimestampColumn] == rows[start].Row[t.TimestampColumn]))
                            end++;

                        DatabaseTestUtil.Stage(levels[d], id, rows.GetRange(start, end - start).Select(r => r.Row).ToArray());
                        levels[d].MergeAll(t.IsSorted ? rows[start].Row[t.TimestampColumn] : 0);
                        start = end;
                    }

                    var table = (SortedWritesTable)levels[d].GetTable(id);
                    foreach (var (row, decoy) in rows)
                    {
                        if (decoy == false)
                            continue;

                        var found = table.FindRow(row, 0);
                        if (found < 0)
                            throw new InvalidOperationException($"A decoy of table {t.Name} was not stored.");

                        (hidden[d] ??= new List<int>()).Add(found);
                        hiddenCount++;
                    }
                }

                var present = Enumerable.Range(0, depth + 1).Select(d => perLevel[d].Count > 0 || random.Next(2) == 0).ToArray();
                var frozen = new List<int>?[depth + 1];
                var live = new List<int>?[depth + 1];
                for (var d = 0; d <= depth; d++)
                {
                    if (hidden[d] is null)
                        continue;

                    if (random.Next(2) == 0)
                        frozen[d] = hidden[d];
                    else
                        live[d] = hidden[d];
                }

                layers.Define(id, present, frozen, live);
            }

            layers.Seal();
            levels[0].AttachView(layers);
            return (levels[0], hiddenCount);
        }

        /// <summary>
        /// Adds the rule of <paramref name="query"/> under <paramref name="config"/> that records its matches (tagged
        /// <paramref name="tag"/>) and does nothing else; <paramref name="extras"/> constrain atoms as atom constraints, or,
        /// for a cached configuration, as the extra constraints of <see cref="RuleSetBuilder.AddRuleFromCachedPlan"/>.
        /// </summary>
        /// <returns>The rule, or <c>null</c> when a cached plan's headers proved that it matches nothing.</returns>
        static RuleId? AddRule(Database db, RuleSetBuilder rsb, DiffQuery query, DiffConfig config, uint tag, ExternalFunctionId recorder, IReadOnlyList<(int Atom, Constraint Constraint)> extras)
        {
            if (config.Cached == false)
            {
                var atoms = query.Atoms.Select((a, i) => a.WithConstraints(extras.Where(x => x.Atom == i).Select(x => x.Constraint).ToArray())).ToArray();
                return Build(rsb, query, atoms, config, tag, recorder);
            }

            var scratch = db.NewRuleSet();
            var planned = Build(scratch, query, query.Atoms, config, tag, recorder);
            using var rs = scratch.Build();
            var cached = rs.BuildCachedPlan(planned);
            return rsb.AddRuleFromCachedPlan(cached, extras.Select(x => (new AtomId(x.Atom), x.Constraint)).ToArray());
        }

        /// <summary>
        /// Adds to <paramref name="rsb"/> the rule over <paramref name="atoms"/>, planned under
        /// <paramref name="config"/>, whose action only calls <paramref name="recorder"/> with <paramref name="tag"/>
        /// and the recorded variables.
        /// </summary>
        /// <returns>The rule.</returns>
        static RuleId Build(RuleSetBuilder rsb, DiffQuery query, IReadOnlyList<DiffAtom> atoms, DiffConfig config, uint tag, ExternalFunctionId recorder)
        {
            var q = rsb.NewRule();
            q.Strategy = config.Strategy;
            q.NoDecomposition = config.NoDecomposition;
            for (var i = 0; i < query.VariableCount; i++)
                q.NewVariable("v" + i);

            foreach (var atom in atoms)
                q.AddAtom(new TableId(atom.Table), atom.Entries, atom.Constraints);

            var rb = q.Build();
            var args = new List<QueryEntry> { QueryEntry.Const(new Value(tag)) };
            args.AddRange(query.RecordedVariables().Select(v => QueryEntry.Var(new Variable(v))));
            rb.CallExternal(recorder, args.ToArray());
            return rb.Build("r" + tag);
        }

    }

}
