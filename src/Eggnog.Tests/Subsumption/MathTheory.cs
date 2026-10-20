using Eggnog.Tests.Support;

namespace Eggnog.Tests.Subsumption
{

    /// <summary>
    /// The theory of the subsumption tests, after egglog's: <c>Math</c> terms over numbers and variables, with sums and
    /// products, every constructor subsumable; a relation <c>seen</c> over numbers, which is not; and an empty ruleset.
    /// </summary>
    public sealed class MathTheory
    {

        /// <summary>
        /// The options of a subsumable table.
        /// </summary>
        public static readonly TableOptions Subsumable = new() { Subsumable = true };

        /// <summary>Gets the options of a relation or constructor that opts out of subsumption.</summary>
        public static readonly TableOptions NotSubsumable = new() { Subsumable = false };

        /// <summary>
        /// Defines the theory in a new graph.
        /// </summary>
        public MathTheory()
        {
            Graph = new EGraph();
            Graph.DefineSort<Node>("Math");
            Num = Graph.DefineConstructor<long, Node>("Num", 1, Subsumable);
            Var = Graph.DefineConstructor<string, Node>("Var", 1, Subsumable);
            Add = Graph.DefineConstructor<Node, Node, Node>("Add", 1, Subsumable);
            Mul = Graph.DefineConstructor<Node, Node, Node>("Mul", 1, Subsumable);
            Seen = Graph.DefineRelation<long>("seen", NotSubsumable);
            Rules = Graph.DefineRuleset("rules");
        }

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets the root.</summary>
        public Scope Root => Graph.Root;

        /// <summary>Gets <c>(Num n)</c>.</summary>
        public Constructor<long, Node> Num { get; }

        /// <summary>Gets <c>(Var name)</c>.</summary>
        public Constructor<string, Node> Var { get; }

        /// <summary>Gets <c>(Add x y)</c>.</summary>
        public Constructor<Node, Node, Node> Add { get; }

        /// <summary>Gets <c>(Mul x y)</c>.</summary>
        public Constructor<Node, Node, Node> Mul { get; }

        /// <summary>Gets the relation <c>seen</c> over numbers, which opts out of subsumption.</summary>
        public Relation<long> Seen { get; }

        /// <summary>Gets an empty ruleset.</summary>
        public Ruleset Rules { get; }

        /// <summary>
        /// Defines <c>see</c> in <see cref="Rules"/>: <c>(Num n) =&gt; (seen n)</c>.
        /// </summary>
        public void DefineSee()
        {
            Rules.DefineRule("see", r =>
            {
                var n = r.Var<long>("n");
                r.When(Fact.Exists(Num.Of(n)));
                r.Then(Seen.Of(n));
            });
        }

    }

}
