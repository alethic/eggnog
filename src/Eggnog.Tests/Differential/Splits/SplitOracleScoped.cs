using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Cosmos;
using Eggnog.Tests.Cosmos.Scoped;

namespace Eggnog.Tests.Differential.Splits
{

    /// <summary>
    /// Runs a split-oracle program in the scoped encoding with engine splits: the schema compiled in its own scope with
    /// the theory's <see cref="CosmosTheory.Facts"/> and frozen; the query in a child of it, with the prototype's
    /// schedule, facts settled before the split, and the unguarded schema split, so that both encodings split the same
    /// keys.
    /// </summary>
    public static class SplitOracleScoped
    {

        /// <summary>
        /// Runs the program and reads what its query proves.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="report">The query's run report.</param>
        /// <returns>The facts.</returns>
        public static SplitOracleFacts Run(SplitOracleProgram program, out RunReport report)
        {
            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            foreach (var (path, values) in program.Discriminators)
                schema.Assume(c.Present.Of(path), c.Dom.Of(path).Is(ValueSet.Of(values)), c.Discriminator.Of(path));

            foreach (var branch in program.Branches)
            {
                var consequences = Consequences(c, branch);
                if (consequences.Length > 0)
                    c.DeclareBranch(schema, branch.Discriminator, branch.Value, consequences);
            }

            var compiled = schema.Run(c.Facts);
            if (compiled.StopReason != RunStopReason.Completed || schema.Children.Count > 0)
                throw new InvalidOperationException($"The schema's run ended with {compiled}.");

            schema.Freeze();
            using var query = schema.CreateChild(c.Query);
            var q = query.Make(Where(c, program));
            query.Assume(c.WhereClause.Of(q));
            var settle = Schedule.Saturate(c.Horn, c.Lattice);
            report = query.Run(Schedule.Saturate(settle, Schedule.Split(c.SchemaSplit, settle), c.Rewrite));
            if (report.StopReason != RunStopReason.Completed || query.Children.Count > 0)
                throw new InvalidOperationException($"The query's run ended with {report} and {query.Children.Count} children.");

            var facts = new SplitOracleFacts { Contradictory = query.IsContradictory };
            foreach (var path in program.Mentioned)
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
        /// The facts a branch declares.
        /// </summary>
        static Fact[] Consequences(CosmosTheory c, SplitOracleBranch branch) =>
            branch.Present.Select(p => c.Present.Of(p))
                .Concat(branch.Forms.Select(f => c.Form.Of(f.Path, f.Form)))
                .Concat(branch.Domains.Select(d => c.Dom.Of(d.Path).Is(ValueSet.Of(d.Values))))
                .ToArray();

        /// <summary>
        /// The query's WHERE clause: the conjuncts joined by <c>And</c>.
        /// </summary>
        static ConstructorTerm<Pred> Where(CosmosTheory c, SplitOracleProgram program)
        {
            var terms = new List<ConstructorTerm<Pred>>();
            foreach (var conjunct in program.Conjuncts)
            {
                switch (conjunct.Kind)
                {
                    case SplitOracleConjunctKind.In:
                        var any = c.StrEq.Of(conjunct.Path, conjunct.Values[0]);
                        foreach (var value in conjunct.Values.Skip(1))
                            any = c.Or.Of(any, c.StrEq.Of(conjunct.Path, value));

                        terms.Add(any);
                        break;
                    case SplitOracleConjunctKind.NotEqual:
                        terms.Add(c.Not.Of(c.StrEq.Of(conjunct.Path, conjunct.Values[0])));
                        break;
                    default:
                        terms.Add(c.UuidEq.Of(conjunct.Path, conjunct.Values[0]));
                        break;
                }
            }

            var where = terms[0];
            foreach (var term in terms.Skip(1))
                where = c.And.Of(where, term);

            return where;
        }

    }

}
