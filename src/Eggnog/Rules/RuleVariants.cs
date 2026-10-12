using System;
using System.Collections.Generic;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// The engine variants of one compiled rule (D36): the semi-naive variant (or the naive one for a naive program) and,
    /// on demand, the naive twin of a semi-naive program, which differential tests use. In R1 a variant is lowered again,
    /// with the description suffix <c>/k{generation}</c>, when the canonical forms of its class constants change (§6.4.5).
    /// </summary>
    /// <remarks>
    /// Thread-safe: a lock-free check of the cached variant, then a double check under a per-rule lock taken after the
    /// graph's engine lock (lock order: definition lock, engine lock, rule lock).
    /// </remarks>
    internal sealed class RuleVariants
    {

        readonly object sync = new();
        readonly RuleProgram program;
        readonly Rule? rule;
        readonly string description;
        readonly bool naive;
        volatile LoweredRule? semiNaiveVariant;
        volatile LoweredRule? naiveVariant;
        int semiNaiveGeneration;
        int naiveGeneration;

        /// <summary>
        /// Initializes a new instance; nothing is lowered yet.
        /// </summary>
        /// <param name="program">The program.</param>
        /// <param name="rule">The rule; <c>null</c> for a query, a split matcher or a copy rule.</param>
        /// <param name="description">The base description (§6.4.3).</param>
        /// <param name="naive">Whether the program is naive: its default variant is the naive one.</param>
        public RuleVariants(RuleProgram program, Rule? rule, string description, bool naive)
        {
            this.program = program;
            this.rule = rule;
            this.description = description;
            this.naive = naive;
        }

        /// <summary>
        /// Gets the base description.
        /// </summary>
        public string Description => description;

        /// <summary>
        /// Gets every variant lowered and not freed.
        /// </summary>
        public IEnumerable<LoweredRule> All
        {
            get
            {
                if (semiNaiveVariant is { } semi)
                    yield return semi;

                if (naiveVariant is { } twin)
                    yield return twin;
            }
        }

        /// <summary>
        /// Gets the default variant (semi-naive, or naive for a naive program); <c>null</c> before it exists.
        /// </summary>
        public LoweredRule? Default => naive ? naiveVariant : semiNaiveVariant;

        /// <summary>
        /// Gets the variant for the mode, lowering it with <paramref name="constants"/> when it does not exist or was
        /// lowered with other constants; a stale variant is freed first (R1 only, D3).
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="semiNaive">Whether the semi-naive variant is wanted; a naive program has none.</param>
        /// <param name="constants">The values to lower the class constants with.</param>
        /// <returns>The variant.</returns>
        public LoweredRule Get(EGraph graph, bool semiNaive, ReadOnlySpan<uint> constants)
        {
            if (naive)
                semiNaive = false;

            var current = semiNaive ? semiNaiveVariant : naiveVariant;
            if (current is not null && constants.SequenceEqual(current.Constants))
                return current;

            lock (graph.EngineLock)
            {
                lock (sync)
                {
                    current = semiNaive ? semiNaiveVariant : naiveVariant;
                    if (current is not null && constants.SequenceEqual(current.Constants))
                        return current;

                    int generation;
                    if (semiNaive)
                    {
                        if (current is not null)
                            semiNaiveGeneration++;

                        generation = semiNaiveGeneration;
                    }
                    else
                    {
                        if (current is not null)
                            naiveGeneration++;

                        generation = naiveGeneration;
                    }

                    if (current is not null)
                        FreeVariant(graph, current);

                    var name = description;
                    if (semiNaive == false && naive == false)
                        name += "/naive";

                    if (generation > 0)
                        name += "/k" + generation.ToString(CultureInfo.InvariantCulture);

                    var lowered = RuleLowering.Lower(graph, program, rule, semiNaive, constants, name);
                    if (semiNaive)
                        semiNaiveVariant = lowered;
                    else
                        naiveVariant = lowered;

                    return lowered;
                }
            }
        }

        /// <summary>
        /// Frees every variant: <c>FreeRule</c>, then <c>FreeExternalFunction</c> of its owned call sites.
        /// </summary>
        /// <param name="graph">The graph.</param>
        public void Free(EGraph graph)
        {
            lock (graph.EngineLock)
            {
                lock (sync)
                {
                    if (semiNaiveVariant is { } semi)
                        FreeVariant(graph, semi);

                    if (naiveVariant is { } twin)
                        FreeVariant(graph, twin);

                    semiNaiveVariant = null;
                    naiveVariant = null;
                }
            }
        }

        /// <summary>
        /// Frees one variant. Called under the engine lock.
        /// </summary>
        static void FreeVariant(EGraph graph, LoweredRule variant)
        {
            graph.Engine.FreeRule(variant.Id);
            foreach (var function in variant.Owned)
                graph.Engine.FreeExternalFunction(function);

            graph.VariantsByDescription.TryRemove(variant.Description, out _);
        }

    }

}
