using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Runs a differential program against the engine's <see cref="Database"/> and the <see cref="DiffModel"/> in
    /// lockstep, comparing every table after every step, and provides the operations steps share: staging writes into
    /// both, building differential rules, staging their actions into the model, and checking their matches.
    /// </summary>
    /// <remarks>
    /// Every write carries the current <see cref="Epoch"/>, in the timestamp column of the rows it writes and as the
    /// timestamp of the merge, and steps advance it, so sorted columns stay nondecreasing and union rows stay in
    /// timestamp order as the engine requires. Rules read the epoch from a database counter kept equal to it
    /// (<see cref="RuleBuilder.ReadCounter"/>), as egglog-bridge reads its timestamp counter, so that a cached plan
    /// stamps rows with the epoch of the run that instantiates it, not of the run that planned it.
    /// </remarks>
    sealed class DiffRunner
    {

        readonly TableId[] ids;
        readonly CounterId epochCounter;

        /// <summary>
        /// Initializes a runner: creates the database in the program's mode, its tables, and the empty model.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="profile">The profile, for the oracle's budget.</param>
        /// <param name="stats">Receives what the program exercised.</param>
        public DiffRunner(DiffProgram program, DiffProfile profile, DiffStats stats)
        {
            Program = program;
            Profile = profile;
            Stats = stats;
            Db = ExecutionTestUtil.NewDatabase(program.Mode);
            ids = new TableId[program.Tables.Count];
            var uf = program.UnionFind >= 0 ? new TableId(program.UnionFind) : default;
            foreach (var t in program.Tables)
            {
                var reads = t.ReadDependencies.Select(i => new TableId(i)).ToArray();
                var writes = t.WriteDependencies.Select(i => new TableId(i)).ToArray();
                ids[t.Index] = Db.AddTable(t.Create(uf), reads, writes, t.Name);
                if (ids[t.Index].Index != t.Index)
                    throw new InvalidOperationException("Engine table ids must follow the program's order.");
            }

            epochCounter = Db.AddCounter();
            Model = new DiffModel(program);
        }

        /// <summary>
        /// Gets the program.
        /// </summary>
        public DiffProgram Program { get; }

        /// <summary>
        /// Gets the profile.
        /// </summary>
        public DiffProfile Profile { get; }

        /// <summary>
        /// Gets the statistics.
        /// </summary>
        public DiffStats Stats { get; }

        /// <summary>
        /// Gets the engine's database; <see cref="DiffCloneStep"/> replaces it.
        /// </summary>
        public Database Db { get; private set; }

        /// <summary>
        /// Gets the model.
        /// </summary>
        public DiffModel Model { get; }

        /// <summary>
        /// Gets the epoch: the timestamp of the next write.
        /// </summary>
        public uint Epoch { get; private set; }

        /// <summary>
        /// Gets the index of the step being run.
        /// </summary>
        public int CurrentStep { get; private set; } = -1;

        /// <summary>
        /// Gets how long each step ran, comparison included, in step order; for <see cref="DifferentialTests.Replay"/>.
        /// </summary>
        public List<TimeSpan> StepTimes { get; } = new();

        /// <summary>
        /// Gets the engine id of program table <paramref name="table"/>.
        /// </summary>
        public TableId Id(int table) => ids[table];

        /// <summary>
        /// Runs every step, comparing all tables after each.
        /// </summary>
        /// <exception cref="DiffMismatchException">The engine disagreed with the model.</exception>
        public void Run()
        {
            Stats.Programs++;
            for (var i = 0; i < Program.Steps.Count; i++)
            {
                CurrentStep = i;
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                Program.Steps[i].Execute(this);
                Stats.Steps++;
                CompareAll();
                StepTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start));
            }

            foreach (var t in Program.Tables)
                Stats.Generations += Db.GetTable(Id(t.Index)).Version.Major;
        }

        /// <summary>
        /// Advances the epoch and the counter rules read it from.
        /// </summary>
        public void Advance()
        {
            Epoch++;
            Db.IncrementCounter(epochCounter);
        }

        /// <summary>
        /// Replaces the database with its clone (<see cref="Database.Clone"/>), configured for the program's mode.
        /// </summary>
        public void CloneDatabase()
        {
            var clone = Db.Clone();
            ExecutionTestUtil.Configure(clone, Program.Mode);
            Db = clone;
        }

        /// <summary>
        /// Stages <paramref name="batch"/> into the engine (one buffer per table, through the database so the tables are
        /// notified) and the model, merges both at the current epoch, and checks that they agree on whether anything
        /// changed.
        /// </summary>
        /// <param name="batch">The writes.</param>
        /// <param name="what">What is being merged, for the failure message.</param>
        /// <returns>Whether the merge changed anything.</returns>
        public bool Merge(DiffBatch batch, string what)
        {
            foreach (var (table, (removals, inserts)) in batch.Tables)
            {
                var buffer = Db.NewBuffer(Id(table));
                foreach (var key in removals)
                    buffer.StageRemove(key);
                foreach (var row in inserts)
                    buffer.StageInsert(row);

                buffer.Flush();
            }

            var engine = Db.MergeAll(Epoch);
            var model = Model.Merge(batch, Epoch);
            if (engine != model)
                Fail($"MergeAll after {what} reported changed={engine}, the model {model}");

            return engine;
        }

        /// <summary>
        /// Compares every table's live rows and row count, and the union-find's canonical ids, with the model.
        /// </summary>
        public void CompareAll()
        {
            foreach (var t in Program.Tables)
            {
                var table = Db.GetTable(Id(t.Index));
                var engine = TableTestUtil.LiveRows(table);
                var model = Model.Rows(t.Index);
                Stats.RowsCompared += model.Count;
                engine.Sort(DatabaseTestUtil.CompareRows);
                model.Sort(DatabaseTestUtil.CompareRows);
                if (engine.Count != model.Count || engine.Zip(model).Any(p => p.First.AsSpan().SequenceEqual(p.Second) == false))
                    Fail($"table {t.Name} differs from the model{Environment.NewLine}{Diff(model, engine)}");
                if (table.Count != model.Count)
                    Fail($"table {t.Name} reports Count {table.Count} but holds {model.Count} live rows");
            }

            if (Model.UnionFind is { } uf)
            {
                var displaced = (DisplacedTable)Db.GetTable(Id(Program.UnionFind));
                foreach (var id in uf.Ids.Concat(Enumerable.Range(0, Program.Domain).Select(i => (uint)i)).Distinct())
                    if (displaced.FindNaive(id) != uf.Find(id))
                        Fail($"the union-find maps {id} to {displaced.FindNaive(id)}, the model to {uf.Find(id)}");
            }
        }

        /// <summary>
        /// Throws a <see cref="DiffMismatchException"/> naming the current step and epoch.
        /// </summary>
        public void Fail(string message) => throw new DiffMismatchException($"step {CurrentStep} (epoch {Epoch}): {message}");

        /// <summary>
        /// Computes the oracle's matches of <paramref name="atoms"/> on the model, projected onto the recorded variables
        /// (<see cref="DiffQuery.RecordedVariables"/>), or <c>null</c> when the query's full join is over the profile's
        /// budget (row visits or distinct matches before projection, which bound the engine's work too).
        /// </summary>
        public Dictionary<string, uint[]>? Reference(DiffQuery query, IReadOnlyList<DiffAtom> atoms)
        {
            var result = DiffReferenceJoin.Join(Model, atoms, query.BoundVariables(), query.SlotCount, Profile.ReferenceBudget, Profile.MaxMatches);
            if (result is null)
            {
                Stats.SkippedQueries++;
                return null;
            }

            return query.IsProjected ? DiffReferenceJoin.Project(result, query.RecordedVariables()) : result;
        }

        /// <summary>
        /// Adds the rule for <paramref name="query"/> under <paramref name="config"/> to <paramref name="rsb"/>, tagged
        /// <paramref name="tag"/> in the recorder; <paramref name="extras"/> constrain atoms further, either as atom
        /// constraints or, for a cached configuration, as the extra constraints of
        /// <see cref="RuleSetBuilder.AddRuleFromCachedPlan"/>.
        /// </summary>
        /// <returns>The rule, or <c>null</c> when the cached plan's headers proved that it matches nothing.</returns>
        public RuleId? AddRule(RuleSetBuilder rsb, DiffQuery query, DiffConfig config, uint tag, ExternalFunctionId recorder, IReadOnlyList<(int Atom, Constraint Constraint)> extras)
        {
            var description = "r" + tag;
            if (query.IsProjected)
                Stats.ProjectedRules++;

            if (config.Cached == false)
            {
                var atoms = query.Atoms.Select((a, i) => a.WithConstraints(extras.Where(x => x.Atom == i).Select(x => x.Constraint).ToArray())).ToArray();
                return BuildRule(rsb, query, atoms, config, tag, recorder, description);
            }

            Stats.CachedRules++;
            var scratch = Db.NewRuleSet();
            var planned = BuildRule(scratch, query, query.Atoms, config, tag, recorder, description);
            using var rs = scratch.Build();
            var cached = rs.BuildCachedPlan(planned);
            return rsb.AddRuleFromCachedPlan(cached, extras.Select(x => (new AtomId(x.Atom), x.Constraint)).ToArray());
        }

        /// <summary>
        /// Builds a differential rule: the query's variables and atoms, the recorder call over the recorded variables
        /// (<see cref="DiffQuery.RecordedVariables"/>), and the action.
        /// </summary>
        RuleId BuildRule(RuleSetBuilder rsb, DiffQuery query, IReadOnlyList<DiffAtom> atoms, DiffConfig config, uint tag, ExternalFunctionId recorder, string description)
        {
            var q = rsb.NewRule();
            q.Strategy = config.Strategy;
            q.NoDecomposition = config.NoDecomposition;
            for (var i = 0; i < query.VariableCount; i++)
                if (q.NewVariable("v" + i).Index != i)
                    throw new InvalidOperationException("Rule variables must be numbered densely from zero.");

            foreach (var atom in atoms)
                q.AddAtom(Id(atom.Table), atom.Entries, atom.Constraints);

            var rb = q.Build();
            var args = new List<QueryEntry> { QueryEntry.Const(new Value(tag)) };
            args.AddRange(query.RecordedVariables().Select(v => QueryEntry.Var(new Variable(v))));
            rb.CallExternal(recorder, args.ToArray());

            // The epoch is read at run time, so that cached plans stamp rows with the epoch of the run.
            var epoch = query.Actions.Any(a => a.Entries.Any(IsEpoch)) ? rb.ReadCounter(epochCounter) : default(Variable?);
            var results = new Dictionary<int, Variable>();
            foreach (var a in query.Actions)
            {
                var entries = a.Entries.Select(e => Translate(e, query, results, epoch)).ToArray();
                switch (a.Kind)
                {
                    case DiffActionKind.Insert:
                        rb.Insert(Id(a.Table), entries);
                        break;
                    case DiffActionKind.Remove:
                        rb.Remove(Id(a.Table), entries);
                        break;
                    case DiffActionKind.Lookup:
                        results[a.Result] = rb.Lookup(Id(a.Table), entries, new ColumnId(a.Column));
                        break;
                    case DiffActionKind.AssertEq:
                        rb.AssertEq(entries[0], entries[1]);
                        break;
                    case DiffActionKind.AssertNe:
                        rb.AssertNe(entries[0], entries[1]);
                        break;
                }
            }

            return rb.Build(description);
        }

        /// <summary>
        /// Maps an action operand to the engine: the epoch marker to the variable holding the epoch counter, a lookup
        /// result to the variable the engine created for it.
        /// </summary>
        static QueryEntry Translate(QueryEntry e, DiffQuery query, Dictionary<int, Variable> results, Variable? epoch)
        {
            if (IsEpoch(e))
                return QueryEntry.Var(epoch!.Value);
            if (e.IsVariable && e.IsWildcard == false && e.Variable.Index >= query.VariableCount)
                return QueryEntry.Var(results[e.Variable.Index]);

            return e;
        }

        static bool IsEpoch(QueryEntry e) => e.IsVariable == false && e.Constant.Rep == DiffAction.EpochMarker;

        /// <summary>
        /// Runs the action of <paramref name="query"/> on each of <paramref name="matches"/> in the model, reading the
        /// model as it is now (the run's snapshot): assertions and failed lookups halt a lane; inserts and removals are
        /// staged into <paramref name="batch"/> when one is given.
        /// </summary>
        /// <returns>The keys of the matches whose lanes ran to the end, which are what the engine counts as matches
        /// (egglog's <c>run_instrs</c> returns the lanes left in the mask, core-relations/src/action/mod.rs:600-615).</returns>
        public HashSet<string> StageActions(DiffQuery query, Dictionary<string, uint[]> matches, DiffBatch? batch)
        {
            var survivors = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, binding) in matches)
            {
                var lane = (uint[])binding.Clone();
                var halt = false;
                foreach (var a in query.Actions)
                {
                    var values = a.Entries.Select(e => Evaluate(e, lane)).ToArray();
                    switch (a.Kind)
                    {
                        case DiffActionKind.Insert:
                            batch?.Insert(a.Table, values);
                            break;
                        case DiffActionKind.Remove:
                            batch?.Remove(a.Table, values);
                            break;
                        case DiffActionKind.Lookup:
                            if (Model.Lookup(a.Table, values) is { } row)
                                lane[a.Result] = row[a.Column];
                            else
                                halt = true;
                            break;
                        case DiffActionKind.AssertEq:
                            halt = values[0] != values[1];
                            break;
                        case DiffActionKind.AssertNe:
                            halt = values[0] == values[1];
                            break;
                    }

                    if (halt)
                        break;
                }

                if (halt == false)
                    survivors.Add(key);
            }

            return survivors;
        }

        uint Evaluate(QueryEntry e, uint[] lane)
        {
            if (e.IsVariable == false)
                return e.Constant.Rep == DiffAction.EpochMarker ? Epoch : e.Constant.Rep;

            return lane[e.Variable.Index];
        }

        /// <summary>
        /// Checks the matches the recorder saw for one rule against the oracle's: the same distinct matches; no match
        /// twice when the multiplicity is defined and the plan is a single stage list; and the report's match count equal
        /// to the number of recorded lanes whose action ran to the end (<paramref name="survivors"/>).
        /// </summary>
        /// <remarks>
        /// A decomposed plan may report a match more than once even when every atom's key is bound: a block binds only
        /// the variables of its bag, so two rows of an atom that agree on them put the same bag binding into the
        /// block's materialization twice, and egglog's materializations append without deduplicating
        /// (core-relations/src/free_join/execute.rs:2118-2152). The engine does the same, so multiplicity is checked on
        /// single plans only. A projected query (<see cref="DiffQuery.Projection"/>) has no defined multiplicity either:
        /// a match stands for every completion of its recorded values, and a plan may enumerate each of them or none.
        /// Its distinct matches and its report count are still exact, since its instructions read only recorded values.
        /// </remarks>
        public void CheckMatches(string label, RulePlan plan, Dictionary<string, uint[]> expected, List<string> recorded, long reported, bool multiplicity, HashSet<string> survivors)
        {
            Stats.Rules++;
            Stats.MaxVariables = Math.Max(Stats.MaxVariables, plan.Template.VariableCount);
            Stats.Matches += expected.Count;
            if (expected.Count > 0)
                Stats.RulesWithMatches++;
            if (plan.Template.Kind == PlanKind.Decomposed)
                Stats.DecomposedRules++;
            if (plan.Template is DecomposedPlan decomposed && HasMaterializationLookup(decomposed))
                Stats.LookupRules++;

            var got = new HashSet<string>(recorded, StringComparer.Ordinal);
            if (got.SetEquals(expected.Keys) == false)
            {
                var missing = expected.Keys.Where(k => got.Contains(k) == false).OrderBy(k => k, StringComparer.Ordinal).ToList();
                var extra = got.Where(k => expected.ContainsKey(k) == false).OrderBy(k => k, StringComparer.Ordinal).ToList();
                Fail($"{label}: matches differ from the oracle ({expected.Count} expected, {got.Count} distinct recorded){Environment.NewLine}" +
                    $"  missing {Show(missing.Select(k => "(" + k + ")").ToList())}{Environment.NewLine}  extra   {Show(extra.Select(k => "(" + k + ")").ToList())}{Environment.NewLine}{plan.Template}");
            }

            if (multiplicity && plan.Template.Kind == PlanKind.Single)
            {
                Stats.MultiplicityChecks++;
                if (recorded.Count != expected.Count)
                {
                    var duplicates = recorded.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => $"{g.Key} x{g.Count()}").Take(10);
                    Fail($"{label}: {recorded.Count} matches recorded for {expected.Count} distinct ones, though every atom's key is bound: {string.Join(" ", duplicates)}{Environment.NewLine}{plan.Template}");
                }
            }

            var completed = recorded.Count(survivors.Contains);
            if (reported != completed)
                Fail($"{label}: the report counts {reported} matches, but {completed} of the {recorded.Count} recorded lanes run to the end{Environment.NewLine}{plan.Template}");
        }

        /// <summary>
        /// Gets whether a decomposed plan checks a materialization through a <see cref="MatScanMode.Lookup"/> stage: in
        /// its result block (where it keeps the join constraint of a block whose message variables no single later bag
        /// holds and that has no value variables, a constraint egglog's planner loses:
        /// core-relations/src/free_join/plan.rs:897-904, 993-995) or as an epilogue of a block.
        /// </summary>
        public static bool HasMaterializationLookup(DecomposedPlan plan)
        {
            foreach (var stage in plan.ResultBlock.Concat(plan.Blocks.SelectMany(b => b.Stages)))
                if (stage is FusedIntersectMatStage { Mode: MatScanMode.Lookup })
                    return true;

            return false;
        }

        static string Show(List<string> items) => items.Count == 0 ? "-" : string.Join(" ", items.Take(20)) + (items.Count > 20 ? $" ... ({items.Count} in all)" : "");

        static string Diff(List<uint[]> expected, List<uint[]> actual)
        {
            var e = new HashSet<string>(expected.Select(TableTestUtil.Show));
            var a = new HashSet<string>(actual.Select(TableTestUtil.Show));
            var b = new StringBuilder();
            b.Append("  missing ").Append(Show(e.Where(r => a.Contains(r) == false).ToList())).AppendLine();
            b.Append("  extra   ").Append(Show(a.Where(r => e.Contains(r) == false).ToList())).AppendLine();
            if (expected.Count != e.Count || actual.Count != a.Count)
                b.Append("  (duplicates: model ").Append(expected.Count - e.Count).Append(", engine ").Append(actual.Count - a.Count).Append(')').AppendLine();

            return b.ToString();
        }

    }

}
