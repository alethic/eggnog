using System;
using System.Threading;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A small value-only theory for scope tests: two tags, a fact relation, a derived relation, a lattice function, and
    /// a ruleset, in a serial graph.
    /// </summary>
    public sealed class ScopeTheory
    {

        /// <summary>
        /// Defines the theory in a new graph.
        /// </summary>
        /// <param name="options">The graph's options; <c>null</c> for the default.</param>
        public ScopeTheory(EGraphOptions? options = null)
        {
            Graph = new EGraph(options);
            Outer = Graph.DefineScopeTag("outer");
            Inner = Graph.DefineScopeTag("inner");
            Fact = Graph.DefineRelation<string>("fact");
            Seen = Graph.DefineRelation<string>("seen");
            Best = Graph.DefineFunction<string, long>("best", ValueLattice.Max<long>());
            Edge = Graph.DefineRelation<long, long>("edge");
            Path = Graph.DefineRelation<long, long>("path");
            Rules = Graph.DefineRuleset("rules");
        }

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets the root.</summary>
        public Scope Root => Graph.Root;

        /// <summary>Gets a tag for outer scopes.</summary>
        public ScopeTag Outer { get; }

        /// <summary>Gets a tag for inner scopes.</summary>
        public ScopeTag Inner { get; }

        /// <summary>Gets a fact relation over strings.</summary>
        public Relation<string> Fact { get; }

        /// <summary>Gets a relation rules derive.</summary>
        public Relation<string> Seen { get; }

        /// <summary>Gets a function merged by max.</summary>
        public Function<string, long> Best { get; }

        /// <summary>Gets the edges of a graph.</summary>
        public Relation<long, long> Edge { get; }

        /// <summary>Gets the paths of a graph.</summary>
        public Relation<long, long> Path { get; }

        /// <summary>Gets the ruleset.</summary>
        public Ruleset Rules { get; }

        /// <summary>
        /// Defines the transitive closure of <see cref="Edge"/> into <see cref="Path"/> as global rules.
        /// </summary>
        /// <returns>The step rule.</returns>
        public Rule DefinePaths()
        {
            Rules.DefineRule("edge is path", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(Edge.Of(x, y));
                r.Then(Path.Of(x, y));
            });
            return Rules.DefineRule("path step", r =>
            {
                var (x, y, z) = r.Vars<long, long, long>("x", "y", "z");
                r.When(Path.Of(x, y), Edge.Of(y, z));
                r.Then(Path.Of(x, z));
            });
        }

        /// <summary>
        /// Defines a rule that blocks in its filter until <paramref name="release"/> is set, after setting
        /// <paramref name="entered"/>: <c>fact(x) → seen(x)</c>.
        /// </summary>
        /// <param name="entered">Set when the filter is reached.</param>
        /// <param name="release">Waited on by the filter.</param>
        /// <returns>The rule.</returns>
        public Rule DefineBlocking(ManualResetEventSlim entered, ManualResetEventSlim release) =>
            Rules.DefineRule("blocking", r =>
            {
                var x = r.Var<string>("x");
                r.When(Fact.Of(x));
                r.Where(x, _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return true;
                });
                r.Then(Seen.Of(x));
            });

    }

}
