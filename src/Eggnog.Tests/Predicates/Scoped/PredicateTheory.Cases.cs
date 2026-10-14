namespace Eggnog.Tests.Predicates.Scoped
{

    public sealed partial class PredicateTheory
    {

        /// <summary>Gets the tag of a discriminator split's cases.</summary>
        public ScopeTag Case { get; private set; } = null!;

        /// <summary>
        /// Gets the paths the running scope's predicate terms mention: an analysis, derived from terms alone.
        /// </summary>
        public Relation<string> Mentioned { get; private set; } = null!;

        /// <summary>Gets the split a schema is compiled with: every present discriminator.</summary>
        public SplitRule SchemaSplit { get; private set; } = null!;

        /// <summary>
        /// Gets the split a query runs: the present discriminators its predicate mentions, so that a query does not
        /// split again on every discriminator the frozen schema already split on.
        /// </summary>
        public SplitRule QuerySplit { get; private set; } = null!;

        /// <summary>Gets the schedule a schema is compiled with: facts and splits, no rewrites.</summary>
        public Schedule Facts { get; private set; } = null!;

        /// <summary>
        /// Gets the schedule a query runs: horn, lattice, split and rewrite steps saturated together, with the facts
        /// settled before each split step, so that the split sees the narrowest domain and opens no case that goes
        /// vacuous only once the domain narrows.
        /// </summary>
        public Schedule Saturation { get; private set; } = null!;

        /// <summary>
        /// Defines the split members: the case tag, the analysis <see cref="Mentioned"/> and the rules that derive it,
        /// the schema's and the query's domain splits, and the schedules that take them.
        /// </summary>
        partial void DefineCases()
        {
            Case = Graph.DefineScopeTag("case");

            // A path is mentioned when a comparison or test over it is a term of the running scope.
            Mentioned = Graph.DefineRelation<string>("mentioned", new TableOptions { Role = TableRole.Analysis });
            foreach (var comparison in new[] { StrEq, StrGe, StrGt, UuidEq, InstantGt })
            {
                Horn.DefineRule($"{comparison.Name} mentions its path", r =>
                {
                    var (p, v) = r.Vars<string, string>("p", "v");
                    r.When(Fact.Exists(comparison.Of(p, v)));
                    r.Then(Mentioned.Of(p));
                });
            }

            foreach (var test in new[] { BoolTrue, IsDefined, IsString })
            {
                Horn.DefineRule($"{test.Name} mentions its path", r =>
                {
                    var p = r.Var<string>("p");
                    r.When(Fact.Exists(test.Of(p)));
                    r.Then(Mentioned.Of(p));
                });
            }

            foreach (var bound in new[] { NumGt, NumLt })
            {
                Horn.DefineRule($"{bound.Name} mentions its path", r =>
                {
                    var (p, n) = r.Vars<string, long>("p", "n");
                    r.When(Fact.Exists(bound.Of(p, n)));
                    r.Then(Mentioned.Of(p));
                });
            }

            // One case per value a present discriminator can still hold: the cases cover the scope. A query splits only
            // what it mentions; the schema's own split has already lifted what the schema alone implies.
            SchemaSplit = Graph.DefineDomainSplit("discriminator", Dom, [Discriminator, Present], Case);
            QuerySplit = Graph.DefineDomainSplit("mentioned discriminator", Dom, [Discriminator, Present, Mentioned], Case);

            var settle = Schedule.Saturate(Horn, Lattice);
            Facts = Schedule.Saturate(settle, Schedule.Split(SchemaSplit, settle));
            Saturation = Schedule.Saturate(settle, Schedule.Split(QuerySplit, settle), Rewrite);
        }

    }

}
