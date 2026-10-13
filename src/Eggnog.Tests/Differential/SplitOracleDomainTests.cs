using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog.Tests.Cosmos.Literal;
using Eggnog.Tests.Differential.Splits;

using Xunit;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// The split oracle's domain check where it can be exact: where the prototype's run contradicted no context, no
    /// case went vacuous, so the domain the engine lifts must equal the prototype's, not merely lie within it.
    /// <see cref="SplitOracleTests"/> accepts, for every seed, a domain within the prototype's, because the prototype's
    /// lift joins the domains of vacuous cases too; this keeps equality for the seeds without a vacuous case. 100 seeds
    /// by default; <c>EGGNOG_SEEDS</c> raises it.
    /// </summary>
    public sealed class SplitOracleDomainTests
    {

        /// <summary>
        /// Every seed whose prototype run contradicted no context lifts exactly the prototype's domains.
        /// </summary>
        /// <remarks>
        /// Setting <c>EGGNOG_PROBE_COUNTS</c> makes the test fail with its counts of seeds without a vacuous case and
        /// of narrower domains in the message.
        /// </remarks>
        [Fact]
        public void WithoutAVacuousCaseTheDomainsAreEqual()
        {
            var seeds = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_SEEDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 100;
            var failures = new List<string>();
            var strict = 0;
            var narrower = 0;
            for (var seed = 0; seed < seeds && failures.Count < 3; seed++)
            {
                var program = SplitOracleProgram.Generate(seed);
                var scoped = SplitOracleScoped.Run(program, out _);
                var literal = SplitOracleLiteral.Run(program);
                if (scoped.Contradictory || literal.Contradictory)
                    continue;

                var vacuous = LiteralContradictions(program) > 0;
                foreach (var (path, domain) in scoped.Domains)
                {
                    if (literal.Domains.TryGetValue(path, out var expected) == false || domain.Equals(expected))
                        continue;

                    narrower++;
                    if (vacuous == false)
                        failures.Add($"{program}\n  {path}: scoped {domain}, literal {expected}");
                }

                strict += vacuous ? 0 : 1;
            }

            Assert.True(failures.Count == 0 && Environment.GetEnvironmentVariable("EGGNOG_PROBE_COUNTS") is null, $"{strict} seeds without a vacuous case; {narrower} narrower domains\n" + string.Join("\n\n", failures));
        }

        /// <summary>
        /// Runs the literal encoding again and counts its contradicted contexts.
        /// </summary>
        static long LiteralContradictions(SplitOracleProgram program)
        {
            var p = new PrototypeTheory(new EGraph());
            p.Ground();
            var root = p.Graph.Root;
            foreach (var (path, values) in program.Discriminators)
            {
                root.Assume(p.Present.Of(p.Schema.Of(), path), p.Dom.Of(p.Schema.Of(), path).Is(ValueSet.Of(values)));
                foreach (var value in values)
                    root.Assume(p.Discriminator.Of(path, value));
            }

            foreach (var branch in program.Branches)
            {
                if (branch.Paths.Any() == false)
                    continue;

                p.Horn.DefineRule($"{branch.Discriminator} = {branch.Value}", r =>
                {
                    var x = r.Var<Ctx>("c");
                    r.When(p.Present.Of(x, branch.Discriminator), p.Dom.Of(x, branch.Discriminator).Is(ValueSet.Of(branch.Value)));
                    r.Then(branch.Present.Select(g => p.Present.Of(x, g))
                        .Concat(branch.Forms.Select(f => p.Form.Of(x, f.Path, f.Form)))
                        .Concat(branch.Domains.Select(d => p.Dom.Of(x, d.Path).Is(ValueSet.Of(d.Values))))
                        .ToArray());
                });
            }

            var q = root.Make(Where(p, program));
            root.Assume(p.WhereClause.Of(q));
            root.Run(Schedule.Saturate(p.Horn, p.Lattice, p.Split, p.Rewrite));
            return root.CountRows(p.Contradicts);
        }

        /// <summary>
        /// The query's WHERE clause in the literal encoding, as <see cref="SplitOracleLiteral"/> builds it.
        /// </summary>
        static ConstructorTerm<Cosmos.Pred> Where(PrototypeTheory p, SplitOracleProgram program)
        {
            var terms = new List<ConstructorTerm<Cosmos.Pred>>();
            foreach (var conjunct in program.Conjuncts)
            {
                switch (conjunct.Kind)
                {
                    case SplitOracleConjunctKind.In:
                        var any = p.StrEq.Of(conjunct.Path, conjunct.Values[0]);
                        foreach (var value in conjunct.Values.Skip(1))
                            any = p.Or.Of(any, p.StrEq.Of(conjunct.Path, value));

                        terms.Add(any);
                        break;
                    case SplitOracleConjunctKind.NotEqual:
                        terms.Add(p.Not.Of(p.StrEq.Of(conjunct.Path, conjunct.Values[0])));
                        break;
                    default:
                        terms.Add(p.UuidEq.Of(conjunct.Path, conjunct.Values[0]));
                        break;
                }
            }

            var result = terms[0];
            foreach (var term in terms.Skip(1))
                result = p.And.Of(result, term);

            return result;
        }

    }

}
