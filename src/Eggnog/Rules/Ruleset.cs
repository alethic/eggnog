using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// A named set of rules that schedules run together (<see cref="Schedule.Run(Ruleset[])"/>). Made by
    /// <see cref="EGraph.DefineRuleset(string)"/>.
    /// </summary>
    public sealed class Ruleset
    {

        CompiledRule[] compiled = Array.Empty<CompiledRule>();
        Rule[] rules = Array.Empty<Rule>();
        ReadOnlyCollection<Rule> rulesView = Array.AsReadOnly(Array.Empty<Rule>());

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="name">The name, unique among the graph's rulesets.</param>
        /// <param name="licence">The licence under which its rules are erasing; <c>null</c> for none.</param>
        internal Ruleset(EGraph graph, string name, ScopeTag? licence = null)
        {
            Graph = graph;
            Name = name;
            Licence = licence;
        }

        /// <summary>
        /// Gets the graph the ruleset belongs to.
        /// </summary>
        public EGraph Graph { get; }

        /// <summary>
        /// Gets the name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the tag under which the ruleset's rules are erasing (<see cref="RulesetOptions.Licence"/>); <c>null</c> for
        /// none.
        /// </summary>
        public ScopeTag? Licence { get; }

        /// <summary>
        /// Gets the global rules, in definition order (a snapshot).
        /// </summary>
        public IReadOnlyList<Rule> Rules => Volatile.Read(ref rulesView);

        /// <summary>
        /// Defines a global rule, compiled immediately. Any thread, any time: the rule takes part from the next iteration of
        /// every run.
        /// </summary>
        /// <param name="name">The rule's name, for rendering, statistics and messages.</param>
        /// <param name="define">Collects the rule: When/Where make the body, Then/Make/Rewrite the head.</param>
        /// <returns>The rule.</returns>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        /// <exception cref="RuleDefinitionException">The rule is not valid; nothing is registered.</exception>
        /// <exception cref="InvalidOperationException">Called from a primitive, lattice, filter or cost function; or the
        /// rule's first scoped read of a table creates its mirror, which is a table definition, while another thread is
        /// using the writable root (nothing is registered; once the root is frozen any thread may).</exception>
        public Rule DefineRule(string name, Action<RuleBuilder> define)
        {
            EngineCallMarker.ThrowIfActive();
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(define);
            var builder = new RuleBuilder(Graph, name);
            try
            {
                define(builder);
            }
            finally
            {
                builder.Close();
            }

            lock (Graph.DefinitionLock)
            {
                var rule = RuleCompiler.Compile(Graph, builder.Draft, this, null);
                Add(rule);
                return rule.Rule;
            }
        }

        /// <summary>
        /// Gets a schedule of one iteration of this ruleset: <see cref="Schedule.Run(Ruleset[])"/>.
        /// </summary>
        /// <param name="ruleset">The ruleset.</param>
        public static implicit operator Schedule(Ruleset ruleset) => Schedule.Run(ruleset);

        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;

        /// <summary>
        /// Gets the compiled global rules, in definition order (a snapshot read without locks).
        /// </summary>
        internal CompiledRule[] Compiled => Volatile.Read(ref compiled);

        /// <summary>
        /// Appends a compiled rule, copy-on-write. Called under the definition lock.
        /// </summary>
        /// <param name="rule">The rule.</param>
        internal void Add(CompiledRule rule)
        {
            var nextCompiled = new CompiledRule[compiled.Length + 1];
            compiled.CopyTo(nextCompiled, 0);
            nextCompiled[^1] = rule;
            var nextRules = new Rule[rules.Length + 1];
            rules.CopyTo(nextRules, 0);
            nextRules[^1] = rule.Rule;
            rules = nextRules;
            Volatile.Write(ref compiled, nextCompiled);
            Volatile.Write(ref rulesView, Array.AsReadOnly(nextRules));
        }

    }

}
