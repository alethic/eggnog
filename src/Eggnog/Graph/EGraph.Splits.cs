using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace Eggnog
{

    public sealed partial class EGraph
    {

        readonly Dictionary<string, SplitRule> splitRulesByName = new(StringComparer.Ordinal);
        SplitRule[] splitRules = Array.Empty<SplitRule>();
        ReadOnlyCollection<SplitRule> splitRulesView = Array.AsReadOnly(Array.Empty<SplitRule>());

        /// <summary>
        /// The engine's <c>LiftSpec</c> of the graph's tables, with the table count it was built at: rebuilt by
        /// <see cref="LiftSpecBuilder.Build"/> when a table is defined. Published whole, read with one volatile read.
        /// </summary>
        internal Tuple<int, Relations.LiftSpec>? LiftSpecCache;

        /// <summary>
        /// Gets every split rule, in definition order (a snapshot).
        /// </summary>
        public IReadOnlyList<SplitRule> SplitRules => Volatile.Read(ref splitRulesView);

        /// <summary>
        /// Defines a split rule, compiled immediately: its body (<see cref="SplitBuilder.When"/>,
        /// <c>Where</c>), the set it splits over (<see cref="SplitBuilder.Cases{T}"/>), its key, and the assumptions written
        /// into each case. A split step (<see cref="Schedule.Split(SplitRule, Schedule)"/>) takes it. Any thread, any
        /// time; a first scoped read in its body creates a mirror table, which counts as a table definition.
        /// </summary>
        /// <remarks>
        /// Coverage is the caller's obligation (<see cref="SplitBuilder"/>): wherever the body matches, the cases'
        /// assumptions must cover the scope. The body is also the split's demand: a key it does not match is never
        /// split.
        /// </remarks>
        /// <param name="name">The name, unique among the graph's split rules.</param>
        /// <param name="define">Collects the split rule.</param>
        /// <param name="caseTag">The tag of the cases it makes; <c>null</c> for untagged cases.</param>
        /// <returns>The split rule.</returns>
        /// <exception cref="ArgumentException">The name is empty or taken; the tag belongs to another graph, or is the
        /// root's.</exception>
        /// <exception cref="RuleDefinitionException">The split rule is not valid (the checks of a rule body, and those
        /// of its shape and assumptions); nothing is registered.</exception>
        /// <exception cref="InvalidOperationException">Called from a primitive, lattice, filter or cost function; or a first
        /// scoped read creates a mirror while another thread is using the writable root.</exception>
        public SplitRule DefineSplit(string name, Action<SplitBuilder> define, ScopeTag? caseTag = null)
        {
            EngineCallMarker.ThrowIfActive();
            NameRules.ValidateName(name, nameof(name));
            ArgumentNullException.ThrowIfNull(define);
            if (caseTag is not null)
            {
                CheckTag(caseTag, nameof(caseTag));
                if (ReferenceEquals(caseTag, RootTag))
                    throw new ArgumentException("The root tag is the root's; tag the cases with a tag of their own.", nameof(caseTag));
            }

            var builder = new SplitBuilder(this, name);
            try
            {
                define(builder);
            }
            finally
            {
                builder.Close();
            }

            lock (DefinitionLock)
            {
                if (splitRulesByName.ContainsKey(name))
                    throw new ArgumentException($"A split rule named '{name}' is already defined.", nameof(name));

                var rule = RuleCompiler.CompileSplit(this, builder, caseTag);
                splitRulesByName.Add(name, rule);
                var next = new SplitRule[splitRules.Length + 1];
                splitRules.CopyTo(next, 0);
                next[^1] = rule;
                splitRules = next;
                Volatile.Write(ref splitRulesView, Array.AsReadOnly(next));
                return rule;
            }
        }

        /// <summary>
        /// Defines the domain split: wherever every guard holds of a key <c>k</c> and
        /// <c>domain(k) = d</c> with more than one element, one case per <c>v</c> in <c>d</c>, assuming
        /// <c>domain(k) = {v}</c>; the split's key is <c>k</c>. Its body is <c>g1(k), …, gn(k), d = domain(k),
        /// (set-length d) &gt; 1</c>. Any thread, any time; a guard read through a scoped view (<c>T.In(tag)</c>) is
        /// read as in <see cref="DefineSplit"/>, and its first scoped read creates a mirror table, which counts as a table
        /// definition.
        /// </summary>
        /// <remarks>
        /// Coverage obligation: <c>domain(k) = S</c> must mean "wherever every guard holds of k, k's value is one of S". A
        /// domain only narrows (a lattice meet), so cases made for an older, wider domain still cover the scope.
        /// More guards split less: fewer cases and fewer facts, never an unsound one. The domain function needs a
        /// lattice: without one, a case's <c>domain(k) = {v}</c> would conflict with the wider domain it narrows, in
        /// every case of every split.
        /// </remarks>
        /// <typeparam name="TKey">The key's type.</typeparam>
        /// <typeparam name="TElement">The domain's element type.</typeparam>
        /// <param name="name">The name, unique among the graph's split rules.</param>
        /// <param name="domain">The domain function: <c>k</c>'s possible values.</param>
        /// <param name="guards">The relations that must hold of <c>k</c>; at least one.</param>
        /// <param name="caseTag">The tag of the cases it makes; <c>null</c> for untagged cases.</param>
        /// <returns>The split rule.</returns>
        /// <exception cref="ArgumentException">There is no guard; a guard or the domain belongs to another graph; the domain
        /// is a scoped view (the cases write it, and scoped views are read-only) or has no lattice; the name is empty or
        /// taken; the tag belongs to another graph, or is the root's.</exception>
        /// <exception cref="RuleDefinitionException">The split rule is not valid (a type is not registered).</exception>
        /// <exception cref="InvalidOperationException">Called from a primitive, lattice, filter or cost function; or a first
        /// scoped read of a guard creates a mirror while another thread is using the writable root.</exception>
        public SplitRule DefineDomainSplit<TKey, TElement>(string name, Function<TKey, Set<TElement>> domain,
            IReadOnlyList<Relation<TKey>> guards, ScopeTag? caseTag = null) where TElement : notnull
        {
            ArgumentNullException.ThrowIfNull(domain);
            ArgumentNullException.ThrowIfNull(guards);
            if (guards.Count == 0)
                throw new ArgumentException("A domain split needs at least one guard: the relation that says which keys the domain describes.", nameof(guards));

            if (ReferenceEquals(domain.Graph, this) == false)
                throw new ArgumentException($"Function '{domain}' is not a table of this graph.", nameof(domain));

            if (domain.ReadScope is not null)
                throw new ArgumentException($"Function '{domain}' is a scoped view; a domain split writes its domain in each case, and scoped views are read-only.", nameof(domain));

            if (domain.Lattice is null)
                throw new ArgumentException($"Function '{domain}' has no lattice; a domain split narrows the domain to one element in each case, which only a lattice's merge can do (a function without one would hold two values).", nameof(domain));

            // A guard may be a scoped view: it is read like any atom of DefineSplit's body.
            for (var i = 0; i < guards.Count; i++)
            {
                var guard = guards[i] ?? throw new ArgumentNullException(nameof(guards), $"Guard {i} is null.");
                if (ReferenceEquals(guard.Graph, this) == false)
                    throw new ArgumentException($"Relation '{guard}' is not a table of this graph.", nameof(guards));
            }

            return DefineSplit(name, split =>
            {
                var (k, d) = split.Vars<TKey, Set<TElement>>("k", "d");
                foreach (var guard in guards)
                    split.When(guard.Of(k));

                split.When(d.Is(domain.Of(k)));
                split.Where(Primitives.Greater.Of(Primitives.Count<TElement>().Of(d), 1L));
                var v = split.Cases("v", d);
                split.Key(k);
                split.Assume(domain.Of(k).Is(Primitives.Singleton<TElement>().Of(v)));
            }, caseTag);
        }

    }

}
