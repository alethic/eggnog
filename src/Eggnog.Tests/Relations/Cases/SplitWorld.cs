using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A writable root with the contradiction designated and ids <c>t, f, c, b, d</c>; <c>edge(t, c)</c> and
    /// <c>edge(f, c)</c> hold, and <c>copy</c> is <c>edge → path</c>.
    /// </summary>
    sealed class SplitWorld
    {

        /// <summary>
        /// Initializes the world: designates <c>bottom</c> as the contradiction, defines <c>copy</c>, mints the ids and
        /// flushes the two edges at the root.
        /// </summary>
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

        /// <summary>
        /// Gets the context graph.
        /// </summary>
        public ContextGraph G { get; }

        /// <summary>
        /// Gets the rule <c>edge(x, y) → path(x, y)</c>.
        /// </summary>
        public EGraphRuleId Copy { get; }

        /// <summary>
        /// Gets <c>t</c>, the true branch of the boolean split, with <c>edge(t, c)</c>.
        /// </summary>
        public Value T { get; }

        /// <summary>
        /// Gets <c>f</c>, the false branch of the boolean split, with <c>edge(f, c)</c>.
        /// </summary>
        public Value F { get; }

        /// <summary>
        /// Gets <c>c</c>, the target of both edges.
        /// </summary>
        public Value C { get; }

        /// <summary>
        /// Gets <c>b</c>, the id the boolean split makes <c>t</c> or <c>f</c>.
        /// </summary>
        public Value B { get; }

        /// <summary>
        /// Gets <c>d</c>, the id of the nested split.
        /// </summary>
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

        /// <summary>
        /// Gets a spec that lifts <c>edge</c>, <c>path</c> and <c>mark</c> as relations.
        /// </summary>
        public LiftSpec Spec => new(
            new LiftTable(G.Edge, LiftTableKind.Relation),
            new LiftTable(G.Path, LiftTableKind.Relation),
            new LiftTable(G.Mark, LiftTableKind.Relation));

    }

}
