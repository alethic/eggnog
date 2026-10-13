using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Cosmos.Scoped;
using Eggnog.Tests.Differential.Splits;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// A model-based soundness check of engine splits (review of R3, I33 soundness): the split oracle's programs
    /// (<see cref="SplitOracleProgram"/>) without UUID conjuncts, run in the scoped encoding, against an enumeration of their
    /// models. A model assigns each discriminator one value of its domain that the query's <c>IN</c> lists and disequalities
    /// allow; under it, exactly the branches of the assigned values fire. Every such assignment is a model of the theory
    /// (governed paths are distinct per discriminator, so no two active branches constrain one path, and each branch
    /// domain is non-empty), so whatever the engine lifts must hold in each of them: a lifted <c>present</c> or
    /// <c>form</c> is derived under every model, a lifted <c>dom</c> contains every value a model allows, and a
    /// contradiction means there is no model. The prototype comparison of <see cref="Differential.SplitOracleTests"/>
    /// checks domains only from above (N98); this checks them from below.
    /// </summary>
    public sealed class R3SplitModelOracleTests
    {

        [Fact]
        public void WhatTheSchemaSplitLiftsHoldsInEveryModel() =>
            CheckSeeds(program => RunScoped(program, querySplit: false));

        [Fact]
        public void WhatTheQuerySplitLiftsHoldsInEveryModel() =>
            CheckSeeds(program => RunScoped(program, querySplit: true));

        /// <summary>
        /// Runs the programs of the seeds without UUID conjuncts and checks each against its models.
        /// </summary>
        static void CheckSeeds(Func<SplitOracleProgram, SplitOracleFacts> run)
        {
            var failures = new List<string>();
            var checkedPrograms = 0;
            var governedLifts = 0;
            var refuted = 0;
            var seeds = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_SEEDS"), out var n) && n > 0 ? n : 400;
            for (var seed = 0; seed < seeds && failures.Count < 3; seed++)
            {
                var program = SplitOracleProgram.Generate(seed);
                if (program.Conjuncts.Any(c => c.Kind == SplitOracleConjunctKind.Uuid))
                    continue;

                checkedPrograms++;
                var facts = run(program);
                refuted += facts.Contradictory ? 1 : 0;
                governedLifts += facts.Forms.Count + facts.Domains.Keys.Count(k => k.StartsWith('g'));
                if (Check(program, facts) is { } failure)
                    failures.Add($"{program}\n{failure}\n  scoped: {facts}");
            }

            Assert.True(failures.Count == 0, string.Join("\n\n", failures));

            // Not vacuous: programs are checked, queries refuted, and governed paths gain forms and domains through cases.
            Assert.True(checkedPrograms > 50, $"only {checkedPrograms} programs without UUID conjuncts");
            Assert.True(refuted > 0, "no query was refuted");
            Assert.True(governedLifts > 20, $"{governedLifts} governed forms and domains lifted");
        }

        /// <summary>
        /// The scoped encoding as a container runs it: the schema compiled with its split (<see cref="CosmosTheory.Facts"/>)
        /// and frozen, then the query run with the unguarded schema split, as <see cref="SplitOracleScoped"/> does, or with
        /// <see cref="CosmosTheory.Saturation"/>, whose split takes only the discriminators the query mentions (D43). Facts
        /// are read for every path, discriminators and governed paths alike.
        /// </summary>
        static SplitOracleFacts RunScoped(SplitOracleProgram program, bool querySplit)
        {
            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            foreach (var (path, values) in program.Discriminators)
                schema.Assume(c.Present.Of(path), c.Dom.Of(path).Is(ValueSet.Of(values)), c.Discriminator.Of(path));

            foreach (var branch in program.Branches)
            {
                var consequences = branch.Present.Select(p => c.Present.Of(p))
                    .Concat(branch.Forms.Select(f => c.Form.Of(f.Path, f.Form)))
                    .Concat(branch.Domains.Select(d => c.Dom.Of(d.Path).Is(ValueSet.Of(d.Values))))
                    .ToArray();
                if (consequences.Length > 0)
                    c.DeclareBranch(schema, branch.Discriminator, branch.Value, consequences);
            }

            Assert.Equal(RunStopReason.Completed, schema.Run(c.Facts).StopReason);
            Assert.Empty(schema.Children);
            schema.Freeze();
            using var query = schema.CreateChild(c.Query);
            var terms = new List<ConstructorTerm<Cosmos.Pred>>();
            foreach (var conjunct in program.Conjuncts)
            {
                if (conjunct.Kind == SplitOracleConjunctKind.In)
                {
                    var any = c.StrEq.Of(conjunct.Path, conjunct.Values[0]);
                    foreach (var value in conjunct.Values.Skip(1))
                        any = c.Or.Of(any, c.StrEq.Of(conjunct.Path, value));

                    terms.Add(any);
                }
                else
                {
                    terms.Add(c.Not.Of(c.StrEq.Of(conjunct.Path, conjunct.Values[0])));
                }
            }

            var where = terms[0];
            foreach (var term in terms.Skip(1))
                where = c.And.Of(where, term);

            query.Assume(c.WhereClause.Of(query.Make(where)));
            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            var report = query.Run(querySplit ? c.Saturation : Schedule.Saturate(settle, Schedule.Split(c.SchemaSplit, settle), c.Rewrite));
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.Empty(query.Children);

            var facts = new SplitOracleFacts { Contradictory = query.IsContradictory };
            var paths = program.Discriminators.SelectMany(d => new[] { d.Path, "g" + d.Path[1..] + "0", "g" + d.Path[1..] + "1" });
            foreach (var path in paths)
            {
                if (query.Check(c.Present.Of(path)))
                    facts.Present.Add(path);

                foreach (var form in SplitOracleProgram.Forms)
                    if (query.Check(c.Form.Of(path, form)))
                        facts.Forms.Add(path + ":" + form);

                if (query.TryGet(c.Dom.Of(path), out var domain))
                    facts.Domains[path] = domain;
            }

            return facts;
        }

        /// <summary>
        /// Checks the engine's facts against every model; returns the first violation, or <c>null</c>.
        /// </summary>
        static string? Check(SplitOracleProgram program, SplitOracleFacts facts)
        {
            var models = Models(program).ToList();
            if (facts.Contradictory)
                return models.Count == 0 ? null : $"contradictory, but {Describe(models[0])} is a model";

            foreach (var model in models)
            {
                var active = program.Branches.Where(b => model[b.Discriminator] == b.Value).ToList();
                foreach (var path in facts.Present)
                {
                    var derived = program.Discriminators.Any(d => d.Path == path) || active.Any(b => b.Present.Contains(path));
                    if (derived == false)
                        return $"present({path}) is lifted, but not derived under {Describe(model)}";
                }

                foreach (var form in facts.Forms)
                {
                    var derived = active.Any(b => b.Forms.Any(f => f.Path + ":" + f.Form == form));
                    if (derived == false)
                        return $"form {form} is lifted, but not derived under {Describe(model)}";
                }

                foreach (var (path, domain) in facts.Domains)
                {
                    IEnumerable<string>? allowed = null;
                    if (model.TryGetValue(path, out var value))
                        allowed = [value];
                    else
                    {
                        var constraints = active.SelectMany(b => b.Domains.Where(d => d.Path == path)).ToList();
                        if (constraints.Count > 0)
                            allowed = constraints.Select(d => (IEnumerable<string>)d.Values).Aggregate((a, b) => a.Intersect(b, StringComparer.Ordinal));
                    }

                    if (allowed is null)
                        return $"dom({path}) = {domain} is lifted, but {Describe(model)} leaves {path} unconstrained";

                    foreach (var v in allowed)
                        if (domain.Contains(v) == false)
                            return $"dom({path}) = {domain} is lifted, but {Describe(model)} allows {path} = '{v}'";
                }
            }

            return null;
        }

        /// <summary>
        /// The assignments of the discriminators that the query's conjuncts allow.
        /// </summary>
        static IEnumerable<Dictionary<string, string>> Models(SplitOracleProgram program)
        {
            IEnumerable<Dictionary<string, string>> partial = [new Dictionary<string, string>(StringComparer.Ordinal)];
            foreach (var (path, values) in program.Discriminators)
            {
                var allowed = values.Where(v => program.Conjuncts.All(c => c.Path != path || Allows(c, v))).ToList();
                partial = partial.SelectMany(m => allowed.Select(v => new Dictionary<string, string>(m, StringComparer.Ordinal) { [path] = v })).ToList();
            }

            return partial;
        }

        static bool Allows(SplitOracleConjunct conjunct, string value) => conjunct.Kind switch
        {
            SplitOracleConjunctKind.In => conjunct.Values.Contains(value, StringComparer.Ordinal),
            SplitOracleConjunctKind.NotEqual => conjunct.Values[0] != value,
            _ => true,
        };

        static string Describe(Dictionary<string, string> model) =>
            "{" + string.Join(", ", model.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} = '{p.Value}'")) + "}";

    }

}
