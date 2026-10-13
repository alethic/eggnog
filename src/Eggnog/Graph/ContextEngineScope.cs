using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eggnog
{

    /// <summary>
    /// The engine side of one scope over one engine context (§6.6.6, colors §10): every scope, the root included, is an
    /// <c>EGraphContext</c>, and every member maps onto one context member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Id constants (D3) are the engine's from R2: every Store-backed context runs each class constant of a rule in its
    /// canonical form there, and uses a watermark only when the stored constants equal the current ones (X19, I30), so a
    /// rule's variant is lowered once, with the constants as written, and never again (§6.4.4). R1's re-lowering path is
    /// gone.
    /// </para>
    /// <para>
    /// Host batches stage on the context's bridge side (<c>StageRow</c>, <c>StageUnion</c>, <c>FreshId</c>), whose
    /// <c>FlushUpdates</c> canonicalizes the id cells of every staged row at every level (invariant C; colors §10.4).
    /// </para>
    /// <para>
    /// Engine calls that may run user delegates (runs, queries, flushes) are counted on the graph, so that a query
    /// disposed meanwhile on another thread hands its variants to the graph instead of freeing them under a running call
    /// (D49, §6.4.6). The adapter holds no state of its own beyond the context, and allocates nothing per row.
    /// </para>
    /// </remarks>
    internal sealed class ContextEngineScope : EngineScope
    {

        readonly EGraph graph;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="context">The engine context.</param>
        public ContextEngineScope(EGraph graph, Relations.EGraphContext context)
        {
            this.graph = graph;
            Context = context;
        }

        /// <summary>
        /// Gets the engine context.
        /// </summary>
        public Relations.EGraphContext Context { get; }

        /// <summary>
        /// Gets the runtime of this context (§4.1): its value of the graph's context-local slot.
        /// </summary>
        public ScopeRuntime Runtime => Context.GetLocal(graph.RuntimeSlot);

        /// <inheritdoc />
        public override long Version => Context.Version;

        /// <summary>
        /// Gets a stamp of the rows the context's views show: the sum of the change stamps of the context and its ancestors
        /// (the storage context's view stamp, colors §9.1, I29). It moves whenever the context's own layer or an ancestor's
        /// changes, which <see cref="Version"/> alone does not see. O(depth).
        /// </summary>
        public long ViewStamp => Context.Context.ViewStamp;

        /// <inheritdoc />
        public override bool IsContradictory => Context.IsContradictory;

        /// <inheritdoc />
        public override long OwnRowCount => Context.OwnRowCount;

        /// <inheritdoc />
        public override bool IsFrozen => Context.IsFrozen;

        /// <inheritdoc />
        public override bool IsDisposed => Context.IsDisposed;

        /// <inheritdoc />
        public override bool IsFaulted => Context.IsFaulted;

        /// <inheritdoc />
        public override void EnsureClean() => Context.EnsureClean();

        /// <inheritdoc />
        public override LoweredRule[] PrepareRules(IReadOnlyList<CompiledRule> rules, bool semiNaive)
        {
            graph.FreeDeferred();
            var lowered = new LoweredRule[rules.Count];
            for (var i = 0; i < lowered.Length; i++)
            {
                var rule = rules[i];
                lowered[i] = rule.Variants.Get(graph, semiNaive && rule.Rule.IsNaive == false);
            }

            return lowered;
        }

        /// <inheritdoc />
        public override LoweredRule PrepareVariant(RuleVariants variants, uint[] writtenIdConstants, bool semiNaive)
        {
            graph.FreeDeferred();
            return variants.Get(graph, semiNaive);
        }

        /// <inheritdoc />
        public override EngineIteration RunRules(ReadOnlySpan<Relations.EGraphRuleId> rules)
        {
            graph.EnterEngineCall();
            try
            {
                var iteration = Context.RunRules(rules);
                return new EngineIteration(iteration.Changed, iteration.RebuildChanged, iteration.Stopped, iteration.RuleSetReport, iteration.RebuildTime);
            }
            finally
            {
                graph.ExitEngineCall();
            }
        }

        /// <inheritdoc />
        public override uint FreshId() => Context.FreshId().Rep;

        /// <inheritdoc />
        public override void StageRow(Relations.FunctionId function, ReadOnlySpan<uint> row) =>
            Context.StageRow(function, MemoryMarshal.Cast<uint, Relations.Value>(row));

        /// <inheritdoc />
        public override void StageUnion(uint a, uint b) => Context.StageUnion(new Relations.Value(a), new Relations.Value(b));

        /// <inheritdoc />
        public override bool Flush()
        {
            graph.EnterEngineCall();
            try
            {
                var before = Context.Version;
                Context.FlushUpdates();
                return Context.Version != before;
            }
            finally
            {
                graph.ExitEngineCall();
            }
        }

        /// <inheritdoc />
        public override uint Find(uint id) => Context.Find(new Relations.Value(id)).Rep;

        /// <inheritdoc />
        public override bool CanonicalizeColumns(Span<uint> cells, int width, ReadOnlySpan<int> columns)
        {
            if (width == 0 || columns.IsEmpty || cells.IsEmpty)
                return false;

            var changed = false;
            for (var offset = 0; offset < cells.Length; offset += width)
            {
                foreach (var column in columns)
                {
                    ref var cell = ref cells[offset + column];
                    var canonical = Context.Find(new Relations.Value(cell)).Rep;
                    if (canonical != cell)
                    {
                        cell = canonical;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The context canonicalizes the key's id cells; a class output is mapped through <see cref="Find"/>, which is
        /// the identity on a clean view's rows and keeps the result canonical in any case.
        /// </remarks>
        public override bool TryLookup(Relations.FunctionId function, ReadOnlySpan<uint> key, out uint output)
        {
            if (Context.TryLookup(function, MemoryMarshal.Cast<uint, Relations.Value>(key), out var value) == false)
            {
                output = 0;
                return false;
            }

            var columns = graph.TableOf(function).Columns;
            output = columns[^1].Kind == ColumnKind.Sort ? Context.Find(value).Rep : value.Rep;
            return true;
        }

        /// <inheritdoc />
        public override long TableSize(Relations.FunctionId function) => Context.TableSize(function);

        /// <inheritdoc />
        public override void ForEach(Relations.FunctionId function, Relations.FunctionRowCallback visit) => Context.ForEach(function, visit);

        /// <inheritdoc />
        /// <remarks>
        /// One iteration of the query's naive rule in the context (§6.6.6): its collector stages nothing, so the merge
        /// changes nothing and <see cref="Version"/> stays.
        /// </remarks>
        public override void RunQuery(LoweredRule query)
        {
            graph.EnterEngineCall();
            try
            {
                Context.RunRules([query.Id]);
            }
            finally
            {
                graph.ExitEngineCall();
            }
        }

        /// <inheritdoc />
        public override Relations.Extractor CreateExtractor(Relations.ExtractionSchema schema, Relations.CostModel costs) =>
            Relations.Extractor.CreateOnDemand(schema, Context, costs);

        /// <inheritdoc />
        public override bool TryExtract(Relations.Extractor extractor, Relations.Sort sort, uint id, out Relations.ExtractedTerm? term) =>
            extractor.TryExtractTerm(sort, new Relations.Value(id), out term);

        /// <inheritdoc />
        public override bool TryGetCost(Relations.Extractor extractor, Relations.Sort sort, uint id, out long cost) =>
            extractor.TryGetCost(sort, new Relations.Value(id), out cost);

        /// <inheritdoc />
        public override ContextEngineScope CreateChild(Relations.ContextOptions options) => new(graph, Context.CreateChild(options));

        /// <inheritdoc />
        public override void Freeze(IReadOnlyCollection<Relations.EGraphRuleId> planRules)
        {
            graph.EnterEngineCall();
            try
            {
                Context.Freeze(planRules);
            }
            finally
            {
                graph.ExitEngineCall();
            }
        }

        /// <inheritdoc />
        public override void Dispose() => Context.Dispose();

        /// <inheritdoc />
        public override string ToString() => Context.ToString();

    }

}
