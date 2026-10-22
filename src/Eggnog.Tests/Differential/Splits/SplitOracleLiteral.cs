using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Predicates;
using Eggnog.Tests.Predicates.Literal;

namespace Eggnog.Tests.Differential.Splits
{

    /// <summary>
    /// Runs a split-oracle program in the literal encoding: contexts as data at one root, with splits written as rules
    /// (<see cref="ContextArgumentTheory.Split"/>) and the schedule <c>(saturate horn lattice split rewrite)</c>; the
    /// oracle.
    /// </summary>
    public static class SplitOracleLiteral
    {

        /// <summary>
        /// Runs the program and reads what its query context proves.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <returns>The facts of <c>(Query)</c>.</returns>
        public static SplitOracleFacts Run(SplitOracleProgram program)
        {
            var p = new ContextArgumentTheory(new EGraph());
            p.Ground();
            var root = p.Graph.Root;
            foreach (var (path, values) in program.Discriminators)
            {
                root.Assume(p.Present.Of(p.Schema.Of(), path), p.Dom.Of(p.Schema.Of(), path).Is(Set.Of(values)));
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
                    r.When(p.Present.Of(x, branch.Discriminator), p.Dom.Of(x, branch.Discriminator).Is(Set.Of(branch.Value)));
                    r.Then(branch.Present.Select(g => p.Present.Of(x, g))
                        .Concat(branch.Forms.Select(f => p.Form.Of(x, f.Path, f.Form)))
                        .Concat(branch.Domains.Select(d => p.Dom.Of(x, d.Path).Is(Set.Of(d.Values))))
                        .ToArray());
                });
            }

            var q = root.Make(Predicate(p, program));
            root.Assume(p.WhereClause.Of(q));
            var report = root.Run(Schedule.Saturate(p.Horn, p.Lattice, p.Split, p.Rewrite));
            if (report.StopReason != RunStopReason.Completed)
                throw new InvalidOperationException($"The literal run ended with {report}.");

            var query = p.Query.Of();
            var facts = new SplitOracleFacts { Contradictory = root.Check(p.Contradicts.Of(query)) };
            foreach (var path in program.Mentioned)
            {
                if (root.Check(p.Present.Of(query, path)))
                    facts.Present.Add(path);

                foreach (var form in SplitOracleProgram.Forms)
                    if (root.Check(p.Form.Of(query, path, form)))
                        facts.Forms.Add(path + ":" + form);

                if (root.TryGet(p.Dom.Of(query, path), out var domain))
                    facts.Domains[path] = domain;
            }

            return facts;
        }

        /// <summary>
        /// The query's predicate: the conjuncts joined by <c>And</c>.
        /// </summary>
        static ConstructorTerm<Pred> Predicate(ContextArgumentTheory p, SplitOracleProgram program)
        {
            var terms = new List<ConstructorTerm<Pred>>();
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

            var predicate = terms[0];
            foreach (var term in terms.Skip(1))
                predicate = p.And.Of(predicate, term);

            return predicate;
        }

    }

}
