using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Differential.CaseRules;
using Eggnog.Tests.Predicates.Scoped;

using Xunit;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A model-based soundness check of <see cref="PredicateTheory"/>'s case rules: random programs
    /// (<see cref="CaseRuleProgram"/>) run in the scoped encoding, against an enumeration of their models. A model gives
    /// each discriminator one value of its domain and each path it governs no value (absent), a string, null or a number,
    /// such that what the schema holds of the path holds (a value of its type set, one of the strings of its domain,
    /// and a value at all where it is present), the branch of the discriminator's value holds (a path it declares
    /// present is present, a path whose stored form it declares holds a string or null, a path whose domain it declares
    /// holds one of its strings) and the query's conjuncts hold (an in-set conjunct's path holds one of its values, a
    /// disequality's path is present and holds another, a numeric comparison's path holds a number). Whatever the query
    /// derives must hold in each model: a present path is present in every model, a stored form is declared by the
    /// branch every model selects, a domain holds the value every model gives the path when present, a string for a
    /// governed path, a type set holds the type of every such value, and a contradiction means that there is no model.
    /// 400 seeds by default; <c>EGGNOG_SEEDS</c> raises it. A failing program prints itself.
    /// </summary>
    public sealed class CaseRuleModelOracleTests
    {

        /// <summary>
        /// A governed path's value in a model: none.
        /// </summary>
        const string Absent = "";

        /// <summary>
        /// A governed path's value in a model: JSON's null.
        /// </summary>
        const string Null = "#null";

        /// <summary>
        /// A governed path's value in a model: a number greater than 0.
        /// </summary>
        const string Number = "#number";

        /// <summary>
        /// The values a model may give a governed path: none, a string of a domain, one of no domain, null or a number.
        /// </summary>
        static readonly string[] GovernedValues = [Absent, .. CaseRuleProgram.Strings, "e", Null, Number];

        /// <summary>
        /// What the query derives with the schema's case rules, which reason by every present discriminator, holds in
        /// every model of its program.
        /// </summary>
        [Fact]
        public void WhatTheSchemasCaseRulesDeriveHoldsInEveryModel() =>
            CheckSeeds(mentioned: false);

        /// <summary>
        /// What the query derives with <see cref="PredicateTheory.Saturation"/>, whose case rules reason only by the
        /// discriminators the query mentions, holds in every model of its program.
        /// </summary>
        [Fact]
        public void WhatTheQuerysCaseRulesDeriveHoldsInEveryModel() =>
            CheckSeeds(mentioned: true);

        /// <summary>
        /// Runs the programs of the seeds and checks each against its models.
        /// </summary>
        /// <param name="mentioned">Whether the query runs <see cref="PredicateTheory.Saturation"/>, whose case rules
        /// reason only by the discriminators it mentions, rather than the schema's case rules.</param>
        static void CheckSeeds(bool mentioned)
        {
            var failures = new List<string>();
            var refuted = 0;
            var dropped = 0;
            var byCases = 0;
            var seeds = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_SEEDS"), out var n) && n > 0 ? n : 400;
            for (var seed = 0; seed < seeds && failures.Count < 3; seed++)
            {
                var program = CaseRuleProgram.Generate(seed);
                var (schema, facts) = RunScoped(program, mentioned);
                if (Check(program, facts) is { } failure)
                    failures.Add($"{program}\n{failure}\n  scoped: {facts}");

                if (facts.Contradictory)
                {
                    refuted++;
                    continue;
                }

                // With the query's case rules, a drop or a fact counts only where the query goes beyond the frozen
                // schema, whose own case rules have reasoned by every present discriminator already: so these counts
                // show the query's case rules at work, not what the query inherits.
                var inherited = mentioned ? schema : null;
                foreach (var (path, values) in program.Discriminators)
                {
                    var domain = facts.Domains[path];
                    if (domain.Count < Allowed(program, path, inherited?.Domains[path].ToArray() ?? values).Count())
                        dropped++;

                    // Facts of the governed paths that no conjunct names while the discriminator still has several
                    // values, which no branch rule derives.
                    if (domain.Count > 1)
                        foreach (var governed in CaseRuleProgram.Governed(path).Where(g => program.Conjuncts.All(c => c.Path != g)))
                            byCases += ByCases(program, facts, inherited, governed);
                }
            }

            Assert.True(failures.Count == 0, string.Join("\n\n", failures));

            // Not vacuous: queries are refuted, values of a discriminator are dropped beyond what the query's own
            // conjuncts exclude, and facts hold by the cases of a discriminator that still has several values.
            Assert.True(refuted > 0, "no query was refuted");
            Assert.True(dropped > 0, "no value was dropped");
            Assert.True(byCases > 0, "nothing was derived by cases");
        }

        /// <summary>
        /// Counts the facts of a governed path that the query holds by cases. With <paramref name="inherited"/>, the
        /// facts the frozen schema does not hold: a stored form or a presence it lacks, a domain or a type set it holds
        /// otherwise or not at all. Without, its stored forms; where the schema declares neither a domain nor a type
        /// set of the path, its domain and type set; and its presence, where the schema does not declare it.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="facts">What the query holds.</param>
        /// <param name="inherited">What the frozen schema holds; <c>null</c> to count what the query holds.</param>
        /// <param name="path">The governed path.</param>
        /// <returns>The count.</returns>
        static int ByCases(CaseRuleProgram program, CaseRuleFacts facts, CaseRuleFacts? inherited, string path)
        {
            var count = facts.Forms.Count(f => f.StartsWith(path + ":", StringComparison.Ordinal) && inherited?.Forms.Contains(f) != true);
            if (inherited is not null)
            {
                count += facts.Domains.TryGetValue(path, out var domain) && Held(inherited.Domains, path, domain) == false ? 1 : 0;
                count += facts.Types.TryGetValue(path, out var types) && Held(inherited.Types, path, types) == false ? 1 : 0;
                count += facts.Present.Contains(path) && inherited.Present.Contains(path) == false ? 1 : 0;
                return count;
            }

            var declared = program.Types.Any(t => t.Path == path) || program.Domains.Any(d => d.Path == path);
            count += facts.Domains.ContainsKey(path) && declared == false ? 1 : 0;
            count += facts.Types.ContainsKey(path) && declared == false ? 1 : 0;
            count += facts.Present.Contains(path) && program.Present.Contains(path) == false ? 1 : 0;
            return count;
        }

        /// <summary>
        /// Whether the frozen schema holds a path's domain or type set as the query does.
        /// </summary>
        static bool Held(SortedDictionary<string, Set<string>> schema, string path, Set<string> value) =>
            schema.TryGetValue(path, out var held) && held.Equals(value);

        /// <summary>
        /// The scoped encoding as a schema and its query run it: the schema compiled with its case rules
        /// (<see cref="PredicateTheory.Facts"/>) and frozen, then the query run with the schema's case rules, which reason
        /// by every present discriminator, or with <see cref="PredicateTheory.Saturation"/>, whose case rules reason only
        /// by those the query mentions. Facts are read for every path, discriminators and governed paths alike, in the
        /// frozen schema and in the query.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="mentioned">Whether the query runs <see cref="PredicateTheory.Saturation"/>.</param>
        /// <returns>What the frozen schema holds, and what the query scope holds.</returns>
        static (CaseRuleFacts Schema, CaseRuleFacts Query) RunScoped(CaseRuleProgram program, bool mentioned)
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild();
            foreach (var (path, values) in program.Discriminators)
                schema.Assume(c.Present.Of(path), c.Dom.Of(path).Is(Set.From(values)), c.Discriminator.Of(path));

            foreach (var (path, types) in program.Types)
                schema.Assume(c.Types.Of(path).Is(Set.From(types)));

            foreach (var (path, values) in program.Domains)
                schema.Assume(c.Dom.Of(path).Is(Set.From(values)));

            foreach (var path in program.Present)
                schema.Assume(c.Present.Of(path));

            foreach (var branch in program.Branches.Where(b => b.IsEmpty == false))
                c.DeclareBranch(schema, branch.Discriminator, branch.Value, branch.Present, branch.Forms, branch.Domains.Select(d => (d.Path, Set.From(d.Values))).ToArray());

            Assert.Equal(RunStopReason.Completed, schema.Run(c.Facts).StopReason);
            Assert.Empty(schema.Children);
            schema.Freeze();
            using var query = schema.CreateChild();
            var terms = new List<ConstructorTerm<Predicates.Pred>>();
            foreach (var conjunct in program.Conjuncts)
            {
                switch (conjunct.Kind)
                {
                    case CaseRuleConjunctKind.In:
                        var any = c.StrEq.Of(conjunct.Path, conjunct.Values[0]);
                        foreach (var value in conjunct.Values.Skip(1))
                            any = c.Or.Of(any, c.StrEq.Of(conjunct.Path, value));

                        terms.Add(any);
                        break;
                    case CaseRuleConjunctKind.NotEqual:
                        terms.Add(c.Not.Of(c.StrEq.Of(conjunct.Path, conjunct.Values[0])));
                        break;
                    default:
                        terms.Add(c.NumGt.Of(conjunct.Path, 0L));
                        break;
                }
            }

            var predicate = terms[0];
            foreach (var term in terms.Skip(1))
                predicate = c.And.Of(predicate, term);

            query.Assume(c.WhereClause.Of(query.Make(predicate)));
            var report = query.Run(mentioned ? c.Saturation : Schedule.Saturate(Schedule.Saturate(c.Horn, c.Lattice), c.SchemaCases, c.Rewrite));
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.Empty(query.Children);
            return (Read(c, schema, program), Read(c, query, program));
        }

        /// <summary>
        /// Reads what a scope holds of every path of a program, discriminators and governed paths alike.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="scope">The scope.</param>
        /// <param name="program">The program.</param>
        /// <returns>The facts.</returns>
        static CaseRuleFacts Read(PredicateTheory c, Scope scope, CaseRuleProgram program)
        {
            var facts = new CaseRuleFacts { Contradictory = c.IsContradictory(scope) };
            foreach (var path in program.Discriminators.SelectMany(d => CaseRuleProgram.Governed(d.Path).Prepend(d.Path)))
            {
                if (scope.Check(c.Present.Of(path)))
                    facts.Present.Add(path);

                foreach (var form in CaseRuleProgram.Forms)
                    if (scope.Check(c.Form.Of(path, form)))
                        facts.Forms.Add(path + ":" + form);

                if (scope.TryGet(c.Dom.Of(path), out var domain))
                    facts.Domains[path] = domain;

                if (scope.TryGet(c.Types.Of(path), out var types))
                    facts.Types[path] = types;
            }

            return facts;
        }

        /// <summary>
        /// Checks the derived facts against every model; returns the first violation, or <c>null</c>.
        /// </summary>
        static string? Check(CaseRuleProgram program, CaseRuleFacts facts)
        {
            // A model gives each discriminator and its paths values apart from the other's, so the models of a
            // program are the products of the models of each discriminator's part.
            var parts = program.Discriminators.Select(d => Models(program, d.Path, d.Values).ToList()).ToList();
            if (facts.Contradictory)
                return parts.Any(p => p.Count == 0) ? null : $"contradictory, but {Describe(parts.Select(p => p[0]).Aggregate(Merge))} is a model";

            // With no model every derived fact holds.
            if (parts.Any(p => p.Count == 0))
                return null;

            for (var i = 0; i < parts.Count; i++)
            {
                var discriminator = program.Discriminators[i].Path;
                foreach (var model in parts[i])
                {
                    var branch = program.Branches.Single(b => b.Discriminator == discriminator && b.Value == model[discriminator]);
                    foreach (var path in CaseRuleProgram.Governed(discriminator).Prepend(discriminator))
                    {
                        var value = model[path];
                        var isDiscriminator = path == discriminator;
                        if (facts.Present.Contains(path) && value == Absent)
                            return $"present({path}) is derived, but {Describe(model)} leaves it absent";

                        foreach (var form in facts.Forms.Where(f => f.StartsWith(path + ":", StringComparison.Ordinal)))
                            if (branch.Forms.Any(f => f.Path + ":" + f.Form == form) == false)
                                return $"form {form} is derived, but not declared under {Describe(model)}";

                        if (value == Absent)
                            continue;

                        if (facts.Domains.TryGetValue(path, out var domain) && domain.Contains(value) == false)
                            return $"dom({path}) = {domain} is derived, but {Describe(model)} gives it '{value}'";

                        var type = isDiscriminator ? "string" : TypeOf(value);
                        if (facts.Types.TryGetValue(path, out var types) && types.Contains(type) == false)
                            return $"types({path}) = {types} is derived, but {Describe(model)} gives it a {type}";
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The models of one discriminator's part of a program: each value of its domain the query allows, with each
        /// combination of values of its governed paths that the value's branch and the query allow.
        /// </summary>
        static IEnumerable<Dictionary<string, string>> Models(CaseRuleProgram program, string discriminator, string[] domain)
        {
            var governed = CaseRuleProgram.Governed(discriminator);
            foreach (var value in Allowed(program, discriminator, domain))
            {
                var branch = program.Branches.Single(b => b.Discriminator == discriminator && b.Value == value);
                foreach (var first in GovernedValues.Where(v => Allows(program, branch, governed[0], v)))
                {
                    foreach (var second in GovernedValues.Where(v => Allows(program, branch, governed[1], v)))
                        yield return new Dictionary<string, string>(StringComparer.Ordinal) { [discriminator] = value, [governed[0]] = first, [governed[1]] = second };
                }
            }
        }

        /// <summary>
        /// The values of a discriminator's domain that the query's conjuncts on it allow.
        /// </summary>
        static IEnumerable<string> Allowed(CaseRuleProgram program, string discriminator, string[] domain) =>
            domain.Where(v => program.Conjuncts.Where(c => c.Path == discriminator).All(c => Allows(c, v)));

        /// <summary>
        /// Whether a governed path may hold <paramref name="value"/> under the schema, a branch and the query.
        /// </summary>
        static bool Allows(CaseRuleProgram program, CaseRuleBranch branch, string path, string value)
        {
            if (value == Absent)
                return program.Present.Contains(path) == false && branch.Present.Contains(path) == false && program.Conjuncts.All(c => c.Path != path);

            if (program.Types.Any(t => t.Path == path && t.Types.Contains(TypeOf(value)) == false))
                return false;

            if (program.Domains.Concat(branch.Domains).Any(d => d.Path == path && d.Values.Contains(value) == false))
                return false;

            if (branch.Forms.Any(f => f.Path == path) && value == Number)
                return false;

            return program.Conjuncts.Where(c => c.Path == path).All(c => Allows(c, value));
        }

        /// <summary>
        /// Whether a conjunct allows its path to hold <paramref name="value"/>: an in-set conjunct holds it, a
        /// disequality's path holds something else, and a numeric comparison's holds a number.
        /// </summary>
        static bool Allows(CaseRuleConjunct conjunct, string value) => conjunct.Kind switch
        {
            CaseRuleConjunctKind.In => conjunct.Values.Contains(value),
            CaseRuleConjunctKind.NotEqual => value != Absent && value != conjunct.Values[0],
            _ => value == Number,
        };

        /// <summary>
        /// The JSON type of a governed path's value.
        /// </summary>
        static string TypeOf(string value) => value switch
        {
            Null => "null",
            Number => "number",
            _ => "string",
        };

        /// <summary>
        /// Joins two parts of a model.
        /// </summary>
        static Dictionary<string, string> Merge(Dictionary<string, string> a, Dictionary<string, string> b) =>
            new(a.Concat(b), StringComparer.Ordinal);

        /// <summary>
        /// Renders a model as <c>{path = 'value', ...}</c>, its paths in ordinal order, an absent path left out.
        /// </summary>
        static string Describe(Dictionary<string, string> model) =>
            "{" + string.Join(", ", model.Where(p => p.Value != Absent).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} = '{p.Value}'")) + "}";

    }

}
