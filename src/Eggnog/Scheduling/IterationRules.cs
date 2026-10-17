using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    /// <summary>
    /// The rules of an iteration of one ruleset in the scopes below a scope with local rules (the anchor): the ruleset's
    /// global rules, then its local rules of each scope from the root down to the anchor. The anchor keeps the last one
    /// it made for each ruleset (<see cref="Scope.CachedIterationRules"/>), so the transient children of a frozen
    /// container, which have no local rules of their own, share one list per ruleset and its lowered variants instead of
    /// building them per iteration, also when their schedule alternates rulesets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list records what it was built from: the ruleset's global snapshot and the local snapshot of every scope from
    /// the anchor to the root. Defining a rule replaces a snapshot, so <see cref="Matches"/> sees any change by
    /// reference, in O(depth). Local rules are fixed once their scope freezes; global rules can still be added.
    /// </para>
    /// <para>
    /// Immutable once published, except for the lowered variants (<see cref="Lower"/>), which are computed once per
    /// semi-naive setting and published with volatile writes, so any number of threads may share an instance. A local
    /// rule's variants are freed only when its scope is released, and by then the anchor and every scope that could
    /// read the list have been released: a scope is released only once it has no live descendant, so a disposed anchor
    /// keeps serving the list to its live descendants until the last of them is released.
    /// </para>
    /// </remarks>
    internal sealed class IterationRules : IReadOnlyList<CompiledRule>
    {

        readonly Ruleset ruleset;
        readonly CompiledRule[] global;
        readonly CompiledRule[][] locals;
        readonly CompiledRule[] rules;
        LoweredRule[]? naive;
        Relations.EGraphRuleId[]? naiveIds;
        LoweredRule[]? semiNaive;
        Relations.EGraphRuleId[]? semiNaiveIds;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        IterationRules(Ruleset ruleset, CompiledRule[] global, CompiledRule[][] locals, CompiledRule[] rules)
        {
            this.ruleset = ruleset;
            this.global = global;
            this.locals = locals;
            this.rules = rules;
        }

        /// <summary>
        /// Gets the ruleset the rules belong to, the key the anchor keeps the list under.
        /// </summary>
        public Ruleset Ruleset => ruleset;

        /// <summary>
        /// Gets the number of rules.
        /// </summary>
        public int Count => rules.Length;

        /// <summary>
        /// Gets a rule by position.
        /// </summary>
        /// <param name="index">The position.</param>
        /// <returns>The rule.</returns>
        public CompiledRule this[int index] => rules[index];

        /// <summary>
        /// Builds the rules of <paramref name="ruleset"/> for the scopes at and below <paramref name="anchor"/> that define
        /// no local rules of their own. O(rules + depth); with no local rule of the ruleset on the path, the list is the
        /// global snapshot itself.
        /// </summary>
        /// <param name="ruleset">The ruleset.</param>
        /// <param name="anchor">The nearest scope with local rules.</param>
        /// <returns>The list.</returns>
        public static IterationRules Build(Ruleset ruleset, Scope anchor)
        {
            var global = ruleset.Compiled;
            var depth = 0;
            for (var up = anchor; up is not null; up = up.Parent)
                depth++;

            var locals = new CompiledRule[depth][];
            var k = 0;
            for (var up = anchor; up is not null; up = up.Parent)
                locals[k++] = up.LocalCompiled;

            var rules = new List<CompiledRule>(global);
            for (k = depth - 1; k >= 0; k--)
                foreach (var rule in locals[k])
                    if (ReferenceEquals(rule.Rule.Ruleset, ruleset))
                        rules.Add(rule);

            return new IterationRules(ruleset, global, locals, rules.Count == global.Length ? global : rules.ToArray());
        }

        /// <summary>
        /// Gets whether the list is still the rules of <paramref name="ruleset"/> below <paramref name="anchor"/>: the
        /// same ruleset, and no rule defined since in it or in a scope from the anchor up. O(depth), allocation-free.
        /// </summary>
        /// <param name="ruleset">The ruleset.</param>
        /// <param name="anchor">The nearest scope with local rules.</param>
        /// <returns>Whether the list can be used.</returns>
        public bool Matches(Ruleset ruleset, Scope anchor)
        {
            if (ReferenceEquals(this.ruleset, ruleset) == false || ReferenceEquals(ruleset.Compiled, global) == false)
                return false;

            var k = 0;
            for (var up = anchor; up is not null; up = up.Parent)
                if (k >= locals.Length || ReferenceEquals(up.LocalCompiled, locals[k++]) == false)
                    return false;

            return k == locals.Length;
        }

        /// <summary>
        /// Gets the engine variants of the rules and their ids, as <see cref="EngineScope.PrepareRules"/> gives them,
        /// lowered on the first call for each semi-naive setting and shared afterwards. Frees the graph's deferred variants
        /// first on every call, as <see cref="EngineScope.PrepareRules"/> does.
        /// </summary>
        /// <param name="scope">A scope at or below the anchor, whose engine lowers the variants.</param>
        /// <param name="semiNaive">Whether semi-naive variants are wanted.</param>
        /// <returns>One variant per rule, in order, and their ids; callers must not modify either array.</returns>
        public (LoweredRule[] Lowered, Relations.EGraphRuleId[] Ids) Lower(Scope scope, bool semiNaive)
        {
            if ((semiNaive ? Volatile.Read(ref this.semiNaive) : Volatile.Read(ref naive)) is { } lowered)
            {
                scope.Graph.FreeDeferred();
                return (lowered, semiNaive ? Volatile.Read(ref semiNaiveIds)! : Volatile.Read(ref naiveIds)!);
            }

            lowered = scope.Engine.PrepareRules(rules, semiNaive);
            var ids = new Relations.EGraphRuleId[lowered.Length];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = lowered[i].Id;

            // The ids first, so a reader that sees the variants sees their ids. Threads that race here compute equal arrays:
            // a rule's variant, once lowered, is the same object for every caller.
            if (semiNaive)
            {
                Volatile.Write(ref semiNaiveIds, ids);
                Volatile.Write(ref this.semiNaive, lowered);
            }
            else
            {
                Volatile.Write(ref naiveIds, ids);
                Volatile.Write(ref naive, lowered);
            }

            return (lowered, ids);
        }

        /// <summary>
        /// Enumerates the rules.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public IEnumerator<CompiledRule> GetEnumerator() => ((IEnumerable<CompiledRule>)rules).GetEnumerator();

        /// <summary>
        /// Enumerates the rules.
        /// </summary>
        /// <returns>The enumerator.</returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    }

}
