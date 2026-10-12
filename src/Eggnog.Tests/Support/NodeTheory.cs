namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A small theory over <see cref="Node"/> that the targeted tests share: nullary, literal, unary and binary
    /// constructors, a few relations, and an empty ruleset.
    /// </summary>
    public sealed class NodeTheory
    {

        /// <summary>
        /// Defines the theory in a new graph.
        /// </summary>
        /// <param name="options">The graph's options.</param>
        public NodeTheory(EGraphOptions? options = null)
        {
            Graph = new EGraph(options);
            Graph.DefineSort<Node>("Node");
            A = Graph.DefineConstructor<Node>("A");
            B = Graph.DefineConstructor<Node>("B");
            Lit = Graph.DefineConstructor<long, Node>("Lit");
            Wrap = Graph.DefineConstructor<Node, Node>("Wrap");
            Add = Graph.DefineConstructor<Node, Node, Node>("Add");
            Tag = Graph.DefineRelation<Node>("tag");
            Num = Graph.DefineRelation<long>("num");
            Rules = Graph.DefineRuleset("rules");
        }

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets the root.</summary>
        public Scope Root => Graph.Root;

        /// <summary>Gets <c>(A)</c>.</summary>
        public Constructor<Node> A { get; }

        /// <summary>Gets <c>(B)</c>.</summary>
        public Constructor<Node> B { get; }

        /// <summary>Gets <c>(Lit n)</c>.</summary>
        public Constructor<long, Node> Lit { get; }

        /// <summary>Gets <c>(Wrap x)</c>.</summary>
        public Constructor<Node, Node> Wrap { get; }

        /// <summary>Gets <c>(Add x y)</c>.</summary>
        public Constructor<Node, Node, Node> Add { get; }

        /// <summary>Gets the relation <c>tag</c> over nodes.</summary>
        public Relation<Node> Tag { get; }

        /// <summary>Gets the relation <c>num</c> over numbers.</summary>
        public Relation<long> Num { get; }

        /// <summary>Gets an empty ruleset.</summary>
        public Ruleset Rules { get; }

    }

}
