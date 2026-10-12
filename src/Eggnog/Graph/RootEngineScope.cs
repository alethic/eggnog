using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eggnog
{

    /// <summary>
    /// The root over the bridge's egglog-compatible root API on a bare <c>Database</c> (§6.4.5, §6.6.5). R1 only: R2
    /// replaces it with a context adapter for every scope and deletes it. It is the only file of the rule layer that
    /// calls the bridge's root-only members (<c>Database.CreateExecutionState</c>, <c>GetCanonInUf</c>,
    /// <c>LookupId</c>, <c>TableAction</c>, <c>UnionAction</c>, <c>FlushUpdates</c>, <c>TryTakePanic</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every member that touches the bridge holds the graph's engine lock for its call (§6.12), which serializes the
    /// root's engine calls with concurrent rule definitions; callers hold the root's guard.
    /// </para>
    /// <para>
    /// Id constants (D3): a bare database runs a rule's class constants as written, so a constant that lost a union would
    /// stop matching its class, and an action writing it would store a stale cell. Before every iteration
    /// <see cref="PrepareVariant"/> maps the written constants through <c>GetCanonInUf</c>; a variant lowered with other
    /// values is freed and lowered again (description suffix <c>/k{generation}</c>), and its new engine rule starts at
    /// watermark 0: the full run D3 requires.
    /// </para>
    /// <para>
    /// Host batches are an execution state stamped with the current timestamp, merged by <c>FlushUpdates</c>, which
    /// rebuilds after any union: the mechanism of <c>EGraph.AddValues</c>, with every sort cell canonical when staged
    /// (invariant C).
    /// </para>
    /// </remarks>
    internal sealed class RootEngineScope : EngineScope
    {

        readonly EGraph graph;
        readonly Relations.EGraph engine;
        readonly Relations.UnionAction union;
        Relations.ExecutionState? batch;
        Relations.TableAction?[] actions = Array.Empty<Relations.TableAction?>();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="engine">The graph's bridge e-graph.</param>
        public RootEngineScope(EGraph graph, Relations.EGraph engine)
        {
            this.graph = graph;
            this.engine = engine;
            union = engine.ActionRegistry.UnionAction;
        }

        /// <inheritdoc />
        public override long Version => engine.Database.ChangeStamp;

        /// <inheritdoc />
        public override bool IsContradictory => TryLookup(graph.Contradiction.Function, ReadOnlySpan<uint>.Empty, out _);

        /// <inheritdoc />
        public override void EnsureClean()
        {

        }

        /// <inheritdoc />
        public override LoweredRule[] PrepareRules(IReadOnlyList<CompiledRule> rules, bool semiNaive)
        {
            lock (graph.EngineLock)
            {
                var lowered = new LoweredRule[rules.Count];
                for (var i = 0; i < lowered.Length; i++)
                {
                    var rule = rules[i];
                    lowered[i] = PrepareVariant(rule.Variants, rule.Program.WrittenIdConstants, semiNaive && rule.Rule.IsNaive == false);
                }

                return lowered;
            }
        }

        /// <inheritdoc />
        public override LoweredRule PrepareVariant(RuleVariants variants, uint[] writtenIdConstants, bool semiNaive)
        {
            lock (graph.EngineLock)
            {
                Span<uint> canonical = writtenIdConstants.Length <= 16 ? stackalloc uint[writtenIdConstants.Length] : new uint[writtenIdConstants.Length];
                for (var i = 0; i < canonical.Length; i++)
                    canonical[i] = engine.GetCanonInUf(new Relations.Value(writtenIdConstants[i])).Rep;

                return variants.Get(graph, semiNaive, canonical);
            }
        }

        /// <inheritdoc />
        public override EngineIteration RunRules(ReadOnlySpan<Relations.EGraphRuleId> rules)
        {
            lock (graph.EngineLock)
            {
                var iteration = engine.RunRules(rules);
                return new EngineIteration(iteration.Changed, iteration.RebuildChanged, false, iteration.RuleSetReport, iteration.RebuildTime);
            }
        }

        /// <inheritdoc />
        public override uint FreshId()
        {
            lock (graph.EngineLock)
                return engine.FreshId().Rep;
        }

        /// <inheritdoc />
        public override void StageRow(Relations.FunctionId function, ReadOnlySpan<uint> row)
        {
            lock (graph.EngineLock)
            {
                var columns = graph.TableOf(function).Columns;
                Span<uint> canonical = row.Length <= 32 ? stackalloc uint[row.Length] : new uint[row.Length];
                for (var i = 0; i < row.Length; i++)
                    canonical[i] = columns[i].Kind == ColumnKind.Sort ? engine.GetCanonInUf(new Relations.Value(row[i])).Rep : row[i];

                batch ??= engine.Database.CreateExecutionState(engine.NextTimestamp);
                ActionOf(function).Insert(batch, canonical);
            }
        }

        /// <inheritdoc />
        public override void StageUnion(uint a, uint b)
        {
            lock (graph.EngineLock)
            {
                batch ??= engine.Database.CreateExecutionState(engine.NextTimestamp);
                union.Union(batch, a, b);
            }
        }

        /// <inheritdoc />
        public override bool Flush()
        {
            lock (graph.EngineLock)
            {
                if (batch is not null)
                {
                    batch.Flush();
                    engine.Database.RecordFailure(batch);
                    batch = null;
                }

                var before = Version;
                engine.FlushUpdates();
                if (engine.TryTakePanic(out var message))
                    throw new Relations.PanicException(message);

                return Version != before;
            }
        }

        /// <inheritdoc />
        public override uint Find(uint id)
        {
            lock (graph.EngineLock)
                return engine.GetCanonInUf(new Relations.Value(id)).Rep;
        }

        /// <inheritdoc />
        public override bool TryLookup(Relations.FunctionId function, ReadOnlySpan<uint> key, out uint output)
        {
            lock (graph.EngineLock)
            {
                var columns = graph.TableOf(function).Columns;
                Span<uint> canonical = key.Length <= 32 ? stackalloc uint[key.Length] : new uint[key.Length];
                for (var i = 0; i < key.Length; i++)
                    canonical[i] = columns[i].Kind == ColumnKind.Sort ? engine.GetCanonInUf(new Relations.Value(key[i])).Rep : key[i];

                var found = engine.LookupId(function, MemoryMarshal.Cast<uint, Relations.Value>(canonical));
                if (found is not { } value)
                {
                    output = 0;
                    return false;
                }

                output = columns[^1].Kind == ColumnKind.Sort ? engine.GetCanonInUf(value).Rep : value.Rep;
                return true;
            }
        }

        /// <inheritdoc />
        public override void ForEach(Relations.FunctionId function, Relations.FunctionRowCallback visit)
        {
            lock (graph.EngineLock)
                engine.ForEach(function, visit);
        }

        /// <inheritdoc />
        public override Relations.Extractor CreateExtractor(Relations.ExtractionSchema schema, Relations.CostModel costs)
        {
            lock (graph.EngineLock)
                return Relations.Extractor.CreateOnDemand(schema, costs, Relations.ExtractionOptions.Default);
        }

        /// <inheritdoc />
        public override bool TryExtract(Relations.Extractor extractor, Relations.Sort sort, uint id, out Relations.ExtractedTerm? term)
        {
            lock (graph.EngineLock)
                return extractor.TryExtractTerm(sort, engine.GetCanonInUf(new Relations.Value(id)), out term);
        }

        /// <inheritdoc />
        public override bool TryGetCost(Relations.Extractor extractor, Relations.Sort sort, uint id, out long cost)
        {
            lock (graph.EngineLock)
                return extractor.TryGetCost(sort, engine.GetCanonInUf(new Relations.Value(id)), out cost);
        }

        /// <summary>
        /// Gets the table action of a function, made once.
        /// </summary>
        Relations.TableAction ActionOf(Relations.FunctionId function)
        {
            if (function.Rep >= actions.Length)
                Array.Resize(ref actions, Math.Max(function.Rep + 1, actions.Length * 2));

            return actions[function.Rep] ??= new Relations.TableAction(engine, function);
        }

    }

}
