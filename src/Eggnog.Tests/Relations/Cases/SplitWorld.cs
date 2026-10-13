using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A writable root with the contradiction designated and ids <c>t, f, c, b, d</c>; <c>edge(t, c)</c> and
    /// <c>edge(f, c)</c> hold, and <c>copy</c> is <c>edge → path</c>.
    /// </summary>
    sealed class SplitWorld
    {

        public SplitWorld()
        {
            G = new ContextGraph();
            G.EGraph.DesignateContradiction(G.Bottom);
            Copy = G.EdgeToPath();
            (T, F, C, B, D) = (G.Root.FreshId(), G.Root.FreshId(), G.Root.FreshId(), G.Root.FreshId(), G.Root.FreshId());
            G.Assert(G.Root, G.Edge, T, C);
            G.Assert(G.Root, G.Edge, F, C);
            G.Root.FlushUpdates();
        }

        public ContextGraph G { get; }

        public EGraphRuleId Copy { get; }

        public Value T { get; }

        public Value F { get; }

        public Value C { get; }

        public Value B { get; }

        public Value D { get; }

        /// <summary>
        /// The boolean split of <c>b</c>: case 0 makes it <c>t</c>, case 1 <c>f</c>.
        /// </summary>
        public TestSplitSource BooleanOfB(int index = 0) => new(index, _ => [([B.Rep], 2)], (@case, i, _) => @case.StageUnion(B, i == 0 ? T : F));

        /// <summary>
        /// A split of <c>d</c>, asked for in children only: case 0 marks it, case 1 relates it to <c>c</c>.
        /// </summary>
        public TestSplitSource SplitOfD(int index) => new(index, scope => scope.IsRoot ? [] : [([D.Rep], 2)], (@case, i, _) =>
        {
            if (i == 0)
                G.Assert(@case, G.Mark, D);
            else
                G.Assert(@case, G.Edge, D, C);
        });

        public LiftSpec Spec => new(
            new LiftTable(G.Edge, LiftTableKind.Relation),
            new LiftTable(G.Path, LiftTableKind.Relation),
            new LiftTable(G.Mark, LiftTableKind.Relation));

    }

}
