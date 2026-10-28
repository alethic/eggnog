namespace Eggnog.Tests.Support
{

    /// <summary>
    /// Rules over a <see cref="ScopeTheory"/> that copy what scoped atoms read under its outer tag into tables of the
    /// running scope: <c>fact @outer</c> into <c>outer facts</c>, <c>best @outer</c> into <c>bests</c>.
    /// </summary>
    public sealed class OuterCopies
    {

        /// <summary>
        /// Defines the tables and the copy rules in the theory's ruleset.
        /// </summary>
        /// <param name="t">The theory.</param>
        public OuterCopies(ScopeTheory t)
        {
            Facts = t.Graph.DefineRelation<string>("outer facts");
            Bests = t.Graph.DefineFunction<string, long>("bests", ValueLattice.Max<long>());
            FactRule = t.Rules.DefineRule("copy facts", r =>
            {
                var x = r.Var<string>("x");
                r.When(t.Fact.In(t.Outer).Of(x));
                r.Then(Facts.Of(x));
            });
            BestRule = t.Rules.DefineRule("copy bests", r =>
            {
                var (k, v) = r.Vars<string, long>("k", "v");
                r.When(v.Is(t.Best.In(t.Outer).Of(k)));
                r.Then(Bests.Of(k).Is(v));
            });
        }

        /// <summary>Gets the copies of the outer scope's facts.</summary>
        public Relation<string> Facts { get; }

        /// <summary>Gets the copies of the outer scope's bests.</summary>
        public Function<string, long> Bests { get; }

        /// <summary>Gets the rule that copies facts.</summary>
        public Rule FactRule { get; }

        /// <summary>Gets the rule that copies bests.</summary>
        public Rule BestRule { get; }

    }

}
