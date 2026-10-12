using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Runs the semi-naive variants of one query, the way egglog-bridge splits a rule: with a boundary epoch <c>b</c>
    /// and the atoms over sorted tables <c>s0 &lt; s1 &lt; ...</c>, variant <c>k</c> restricts atom <c>sk</c> to new rows
    /// (<c>ts &gt;= b</c>) and the sorted atoms before it to old rows (<c>ts &lt; b</c>). The restrictions are header
    /// constraints on the sorted timestamp columns, given either as atom constraints or, for cached configurations, as
    /// the extra constraints of <see cref="RuleSetBuilder.AddRuleFromCachedPlan"/>, which skips a variant whose header
    /// selects nothing. Pinned constraints (equalities on data columns, which cached plans promote to headers) apply to
    /// every variant.
    /// </summary>
    /// <remarks>
    /// Every variant's matches are checked against the oracle with the same constraints. The variants together must
    /// cover exactly the matches that use at least one new row: when the multiplicity is defined, the full join's
    /// matches minus the all-old join's; otherwise at least those and at most the full join's. Then the actions of all
    /// variants run, as in <see cref="DiffRunStep"/>.
    /// </remarks>
    sealed class DiffSemiNaiveStep : DiffStep
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <param name="back">How many epochs before the current one the boundary lies.</param>
        /// <param name="configs">The configurations every variant is planned under.</param>
        /// <param name="pinned">Extra constraints every variant gets.</param>
        public DiffSemiNaiveStep(DiffQuery query, int back, DiffConfig[] configs, (int Atom, Constraint Constraint)[] pinned)
        {
            Query = query;
            Back = back;
            Configs = configs;
            Pinned = pinned;
        }

        /// <summary>
        /// Gets the query.
        /// </summary>
        public DiffQuery Query { get; }

        /// <summary>
        /// Gets how many epochs before the current one the boundary lies.
        /// </summary>
        public int Back { get; }

        /// <summary>
        /// Gets the configurations.
        /// </summary>
        public DiffConfig[] Configs { get; }

        /// <summary>
        /// Gets the extra constraints every variant gets.
        /// </summary>
        public (int Atom, Constraint Constraint)[] Pinned { get; }

        /// <summary>
        /// Splits the query at the runner's current boundary: the semi-naive variants and the oracle's matches of each
        /// (an entry is <c>null</c> when that variant is over the oracle's budget). Shared with
        /// <see cref="DiffLayeredSemiNaiveStep"/>, which runs the same variants over a layered copy.
        /// </summary>
        /// <param name="runner">The runner.</param>
        /// <returns>The variants, their matches, the sorted atoms and the boundary.</returns>
        public (List<(int Atom, Constraint Constraint)[]> Variants, Dictionary<string, uint[]>?[] Expected, int[] Sorted, uint Boundary) Split(DiffRunner runner)
        {
            var program = runner.Program;
            var boundary = runner.Epoch > Back ? runner.Epoch - (uint)Back : 0u;
            var sorted = Enumerable.Range(0, Query.Atoms.Length).Where(i => program.Tables[Query.Atoms[i].Table].IsSorted).ToArray();
            var variants = new List<(int Atom, Constraint Constraint)[]>();
            if (sorted.Length == 0)
                variants.Add(Pinned);

            foreach (var k in sorted)
            {
                var extras = new List<(int, Constraint)> { (k, Constraint.GeConst(Ts(program, k), new Value(boundary))) };
                foreach (var j in sorted.Where(j => j < k))
                    extras.Add((j, Constraint.LtConst(Ts(program, j), new Value(boundary))));

                extras.AddRange(Pinned);
                variants.Add(extras.ToArray());
            }

            return (variants, variants.Select(v => runner.Reference(Query, Constrain(v))).ToArray(), sorted, boundary);
        }

        /// <inheritdoc />
        public override void Execute(DiffRunner runner)
        {
            var program = runner.Program;
            var (variants, expected, sorted, boundary) = Split(runner);
            var all = runner.Reference(Query, Constrain(Pinned));
            var old = runner.Reference(Query, Constrain(sorted.Select(j => (j, Constraint.LtConst(Ts(program, j), new Value(boundary)))).Concat(Pinned).ToArray()));
            if (all is null || old is null || expected.Any(e => e is null))
            {
                runner.Advance();
                return;
            }

            // The oracle's own check that the variants are the semi-naive split.
            var union = new Dictionary<string, uint[]>(StringComparer.Ordinal);
            foreach (var e in expected)
                foreach (var (key, binding) in e!)
                    union.TryAdd(key, binding);

            var fresh = all.Keys.Where(k => old.ContainsKey(k) == false).ToHashSet(StringComparer.Ordinal);
            var exact = sorted.Length == 0 || Query.MultiplicityDefined(program);
            if (sorted.Length > 0 && (union.Keys.All(all.ContainsKey) == false || fresh.All(union.ContainsKey) == false || (exact && union.Count != fresh.Count)))
                runner.Fail($"the semi-naive variants at boundary {boundary} do not split the query: {union.Count} variant matches, {fresh.Count} new, {all.Count} in all");

            var recorder = new DiffRecorder();
            var function = runner.Db.AddExternalFunction(recorder.Function);
            var rsb = runner.Db.NewRuleSet();
            var rules = new List<(int Variant, DiffConfig Config, uint Tag, RuleId? Rule)>();
            var tag = 0u;
            foreach (var config in Configs)
            {
                for (var v = 0; v < variants.Count; v++)
                {
                    rules.Add((v, config, tag, runner.AddRule(rsb, Query, config, tag, function, variants[v])));
                    tag++;
                }
            }

            using var rs = rsb.Build();
            var report = runner.Db.RunRuleSet(rs, ReportLevel.TimeOnly, runner.Epoch);
            var multiplicity = Query.MultiplicityDefined(program);
            var batch = new DiffBatch();
            var survivors = runner.StageActions(Query, union, batch);
            foreach (var (v, config, t, rule) in rules)
            {
                var label = $"semi-naive variant {v} ({string.Join(", ", variants[v].Select(x => $"atom {x.Atom}: {x.Constraint}"))}) under {config}";
                runner.Stats.SemiNaiveVariants++;
                if (rule is not { } id)
                {
                    runner.Stats.SkippedVariants++;
                    if (expected[v]!.Count > 0)
                        runner.Fail($"{label}: the variant was skipped as empty, but the oracle has {expected[v]!.Count} matches");

                    continue;
                }

                runner.CheckMatches(label, rs[id], expected[v]!, recorder.Matches(t), report.NumMatches("r" + t), multiplicity, survivors);
            }

            var changed = runner.Model.Merge(batch, runner.Epoch);
            if (report.Changed != changed)
                runner.Fail($"the semi-naive run reported changed={report.Changed}, the model {changed}");

            runner.Db.FreeExternalFunction(function);
            runner.Advance();
        }

        /// <inheritdoc />
        public override string Format(DiffProgram program)
        {
            var pinned = Pinned.Length == 0 ? "" : $" pinned [{string.Join(", ", Pinned.Select(x => $"atom {x.Atom}: {x.Constraint}"))}]";
            return $"semi-naive back={Back} under [{string.Join(" ", Configs)}]{pinned}: {Query.Format(program)}";
        }

        /// <inheritdoc />
        public override IEnumerable<DiffStep> Simplify(DiffProgram program)
        {
            if (Configs.Length > 1)
                foreach (var config in Configs)
                    yield return new DiffSemiNaiveStep(Query, Back, [config], Pinned);

            if (Pinned.Length > 0)
                yield return new DiffSemiNaiveStep(Query, Back, Configs, []);

            // Pinned constraints name atoms by index, so they survive only simplifications that keep every atom.
            foreach (var simpler in Query.Simplify())
                yield return new DiffSemiNaiveStep(simpler, Back, Configs, simpler.Atoms.Length == Query.Atoms.Length ? Pinned : []);
        }

        DiffAtom[] Constrain((int Atom, Constraint Constraint)[] extras) =>
            Query.Atoms.Select((a, i) => a.WithConstraints(extras.Where(x => x.Atom == i).Select(x => x.Constraint).ToArray())).ToArray();

        ColumnId Ts(DiffProgram program, int atom) => new(program.Tables[Query.Atoms[atom].Table].TimestampColumn);

    }

}
