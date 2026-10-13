using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An e-graph with the schema and rules of <see cref="ContextExtractionProgram"/>, which runs a program on a chain
    /// of contexts (<see cref="RunChain"/>) or replays it at its root (<see cref="Replay"/>).
    /// </summary>
    sealed class ContextExtractionWorld
    {

        /// <summary>
        /// Declares the program's constructors, with its costs, and its two rules.
        /// </summary>
        public ContextExtractionWorld(ContextExtractionProgram program)
        {
            Program = program;
            Fx = new ExtractionFixture();
            E = new EqSort("E");
            Functions =
            [
                Fx.Constructor("L0", E, program.Costs[0]),
                Fx.Constructor("L1", E, program.Costs[1]),
                Fx.Constructor("Num", E, program.Costs[2], Fx.I64),
                Fx.Constructor("U", E, program.Costs[3], E),
                Fx.Constructor("W", E, program.Costs[4], E),
                Fx.Constructor("B", E, program.Costs[5], E, E),
            ];

            // B(x, y) = z => B(y, x) = z
            var rb = Fx.EGraph.NewRule("commute", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var z = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(Functions[5], [x, y, z], false);
            rb.Set(Functions[5], [y, x, z]);
            var commute = rb.Build();

            // U(x) = u, W(u) = w => union(w, x)
            rb = Fx.EGraph.NewRule("collapse", true);
            x = rb.NewVar(ColumnTy.Id);
            var u = rb.NewVar(ColumnTy.Id);
            var w = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(Functions[3], [x, u], false);
            rb.QueryTable(Functions[4], [u, w], false);
            rb.Union(w, x);
            Rules = [commute, rb.Build()];
        }

        public ContextExtractionProgram Program { get; }

        public ExtractionFixture Fx { get; }

        public EqSort E { get; }

        public FunctionId[] Functions { get; }

        public EGraphRuleId[] Rules { get; }

        /// <summary>
        /// Runs the program on a chain of contexts, then saturates the deepest one.
        /// </summary>
        /// <returns>The chain, root first, and the id each term was made with.</returns>
        public (List<EGraphContext> Chain, Value[] Terms) RunChain()
        {
            var chain = new List<EGraphContext> { Fx.EGraph.Root };
            var terms = new List<Value>();
            foreach (var step in Program.Steps)
            {
                if (step.Kind == ContextExtractionStepKind.Child)
                {
                    if (step.Freeze)
                        chain[step.Level].Freeze();

                    chain.Add(chain[step.Level].CreateChild());
                    continue;
                }

                Apply(chain[step.Level], step, terms);
            }

            SaturateIn(chain[^1]);
            return (chain, terms.ToArray());
        }

        /// <summary>
        /// Replays every step of the program, in order, at the root, then saturates it.
        /// </summary>
        /// <returns>The id each term was made with.</returns>
        public Value[] Replay()
        {
            var root = Fx.EGraph.Root;
            var terms = new List<Value>();
            foreach (var step in Program.Steps)
                if (step.Kind != ContextExtractionStepKind.Child)
                    Apply(root, step, terms);

            SaturateIn(root);
            return terms.ToArray();
        }

        /// <summary>
        /// Applies a make, union or saturation step in <paramref name="context"/>.
        /// </summary>
        void Apply(EGraphContext context, ContextExtractionStep step, List<Value> terms)
        {
            switch (step.Kind)
            {
                case ContextExtractionStepKind.Make:
                    Value[] key = step.Function switch
                    {
                        2 => [Fx.Int(step.First)],
                        3 or 4 => [terms[step.First]],
                        5 => [terms[step.First], terms[step.Second]],
                        _ => [],
                    };
                    terms.Add(context.AddTerm(Functions[step.Function], key));
                    break;
                case ContextExtractionStepKind.Union:
                    context.StageUnion(terms[step.First], terms[step.Second]);
                    context.FlushUpdates();
                    break;
                default:
                    SaturateIn(context);
                    break;
            }
        }

        /// <summary>
        /// Saturates the rules in <paramref name="context"/>, which they always reach.
        /// </summary>
        void SaturateIn(EGraphContext context)
        {
            var report = context.Saturate(Rules, ContextExtractionProgram.MaxIterations);
            if (report.Saturated == false)
                throw new InvalidOperationException($"Seed {Program.Seed}: the rules did not saturate.");
        }

    }

}
