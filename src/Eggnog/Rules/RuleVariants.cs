using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The engine variants of one compiled rule, query or mirror copy rule (D36): the semi-naive variant (or the naive one
    /// for a naive program) and, on demand, the naive twin of a semi-naive program, which differential tests use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each variant is lowered once, with the class constants as written: the engine runs every Id constant in its
    /// canonical form in the running context and gates watermarks on them (X19, I30), so a variant never changes once
    /// lowered (§6.4.4). R1's re-lowering under <c>/k{generation}</c> (§6.4.5) is gone with the bare root.
    /// </para>
    /// <para>
    /// Thread-safe: a lock-free read of the cached variant, then a double check under a per-rule lock. Lowering registers
    /// engine definitions, which the bridge publishes copy-on-write (X16), so no engine lock is needed.
    /// </para>
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
        /// Gets the default variant (semi-naive, or naive for a naive program), for E10's plan list; <c>null</c> before it
        /// exists or once the variants are freed.
        /// </summary>
        public LoweredRule? Default => naive ? naiveVariant : semiNaiveVariant;

        /// <summary>
        /// Gets the variant for the mode, lowering it with the written class constants the first time. O(1) once
        /// lowered: one volatile read.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="semiNaive">Whether the semi-naive variant is wanted; a naive program has none.</param>
        /// <returns>The variant.</returns>
        public LoweredRule Get(EGraph graph, bool semiNaive)
        {
            if (naive)
                semiNaive = false;

            var current = semiNaive ? semiNaiveVariant : naiveVariant;
            if (current is not null)
                return current;

            lock (sync)
            {
                current = semiNaive ? semiNaiveVariant : naiveVariant;
                if (current is not null)
                    return current;

                var name = description;
                if (semiNaive == false && naive == false)
                    name += "/naive";

                var lowered = RuleLowering.Lower(graph, program, rule, semiNaive, program.WrittenIdConstants, name);
                if (semiNaive)
                    semiNaiveVariant = lowered;
                else
                    naiveVariant = lowered;

                return lowered;
            }
        }

        /// <summary>
        /// Frees every variant: <c>FreeRule</c>, then <c>FreeExternalFunction</c> of its owned call sites. The caller
        /// guarantees that no engine call is using them (§6.4.6).
        /// </summary>
        /// <param name="graph">The graph.</param>
        public void Free(EGraph graph)
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

        /// <summary>
        /// Frees one variant.
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
