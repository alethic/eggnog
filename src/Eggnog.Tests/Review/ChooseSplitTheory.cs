namespace Eggnog.Tests.Review
{

    /// <summary>
    /// A graph whose split rule <c>choose</c> opens one case per element of <c>dom(p)</c> and assumes <c>chosen(p, v)</c>
    /// in it: a fact no rule derives, so it reaches the split scope only through a lift (R3 review).
    /// </summary>
    public sealed class ChooseSplitTheory
    {

        /// <summary>
        /// Defines the theory in a new graph.
        /// </summary>
        /// <param name="scoped">Whether the split's body also reads <c>present@schema(p)</c>.</param>
        public ChooseSplitTheory(bool scoped = false)
        {
            Graph = new EGraph();
            Schema = Graph.DefineScopeTag("schema");
            Present = Graph.DefineRelation<string>("present");
            Dom = Graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            Chosen = Graph.DefineRelation<string, string>("chosen");
            Rules = Graph.DefineRuleset("rules");
            Split = Graph.DefineSplit("choose", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                if (scoped)
                    s.When(Present.In(Schema).Of(p));

                s.When(d.Is(Dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(Chosen.Of(p, v));
            });
        }

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets the tag the scoped split reads.</summary>
        public ScopeTag Schema { get; }

        /// <summary>Gets a relation the scoped split reads under <see cref="Schema"/>.</summary>
        public Relation<string> Present { get; }

        /// <summary>Gets a path's possible values, merged by intersection.</summary>
        public Function<string, ValueSet<string>> Dom { get; }

        /// <summary>Gets the value a case chose for a path.</summary>
        public Relation<string, string> Chosen { get; }

        /// <summary>Gets an empty ruleset, the split's in-case schedule.</summary>
        public Ruleset Rules { get; }

        /// <summary>Gets the split rule.</summary>
        public SplitRule Split { get; }

    }

}
