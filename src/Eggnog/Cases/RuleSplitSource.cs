using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// The engine's split source for one <see cref="SplitRule"/> at one position of a split step's rules: it finds the
    /// splits the rule's body asks for in a scope, and makes each case assume its element.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FindRequests"/> brings the mirrors the matcher reads current, runs the matcher in the scope as a
    /// query (an iteration of the scope that writes nothing), and turns each match, in content order, into a request
    /// keyed on its key cells, with one case per element of its set (in set order); an empty set is skipped and
    /// counted. <see cref="Assume"/> wraps the new case context as a case <see cref="Scope"/> of the split scope and
    /// writes the grounded assumptions into it as one host batch.
    /// </para>
    /// <para>
    /// Stateless beyond its rule and position, so one source serves any number of drivers and runs on any threads; the
    /// driver calls it on the thread of the run, without holding the scope's engine domain, while the run holds the
    /// domain's <see cref="ScopeGuard"/>. Allocation is per request and per case (a request's key and its copy of the
    /// match, which the engine's request holds; the grounded facts of a case), never per row the matcher reads: the
    /// matches themselves go into a pooled buffer, and a set is counted without decoding its elements.
    /// </para>
    /// </remarks>
    internal sealed class RuleSplitSource : Relations.ISplitSource
    {

        readonly EGraph graph;
        readonly SplitRule rule;
        readonly int index;

        /// <summary>
        /// Initializes the source of <paramref name="rule"/> at position <paramref name="index"/> of a split step's rules.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="rule">The split rule.</param>
        /// <param name="index">The rule's position among a split step's rules: its requests' key source.</param>
        public RuleSplitSource(EGraph graph, SplitRule rule, int index)
        {
            this.graph = graph;
            this.rule = rule;
            this.index = index;
        }

        /// <summary>
        /// Gets the split rule.
        /// </summary>
        public SplitRule Rule => rule;

        /// <inheritdoc />
        /// <exception cref="RuleEvaluationException">A primitive or filter of the matcher failed; the scope is
        /// faulted.</exception>
        public void FindRequests(Relations.EGraphContext scope, List<Relations.SplitRequest> requests, Relations.SplitReport report)
        {
            var owner = OwnerOf(scope);
            var program = rule.Program;
            var reads = program.Matcher.ReadScopes;
            if (reads.Length > 0)
                MirrorSync.Ensure(owner, reads, $"Split rule '{rule.Name}'", null);

            var engine = owner.Engine;
            var runtime = owner.Runtime;
            using var matches = new TupleBuffer(program.Width);
            runtime.TakeFailure();
            runtime.QueryMatches = matches;
            runtime.StopAtFirstQueryMatch = false;
            try
            {
                var variant = engine.PrepareVariant(program.Variants, program.Matcher.WrittenIdConstants, false);
                engine.RunQuery(variant);
            }
            catch (Relations.PanicException panic)
            {
                throw owner.FaultWith(panic);
            }
            finally
            {
                runtime.QueryMatches = null;
            }

            // Canonical in the scope, deduplicated, and in content order, so that requests, and so cases, are made in an
            // order that does not depend on the engine's scan order.
            if (engine.CanonicalizeColumns(matches.Cells, matches.Width, program.SortColumns))
                matches.Deduplicate();

            matches.SortByContent(program.Columns);
            var setType = program.Columns[program.SetColumn];
            for (var i = 0; i < matches.Count; i++)
            {
                var tuple = matches[i];
                var cases = program.CountOf(setType, tuple[program.SetColumn]);
                if (cases == 0)
                {
                    // Nothing to split: the theory's own rules derive the contradiction an empty domain means.
                    report.SplitsSkipped++;
                    continue;
                }

                var key = new uint[program.KeyColumns.Length];
                for (var k = 0; k < key.Length; k++)
                    key[k] = tuple[program.KeyColumns[k]];

                // The payload is the match: Assume grounds a case's assumptions from it and the set's element.
                requests.Add(new Relations.SplitRequest(new Relations.SplitKey(index, key), cases, tuple.ToArray()));
            }
        }

        /// <inheritdoc />
        /// <exception cref="RuleEvaluationException">A primitive of an assumption threw; an assumption gives a function
        /// without a lattice a second value, or cannot be written (a conflicting write during a run; the
        /// <see cref="FactConflictException"/> is the inner exception and the case is faulted); or a merge of the
        /// case's batch failed (the case is faulted). <c>Run</c> faults the scope it was called on too.</exception>
        public void Assume(Relations.EGraphContext @case, in Relations.SplitRequest request, int caseIndex)
        {
            var parent = OwnerOf(@case.Parent ?? throw new InvalidOperationException("A case has a parent."));
            var scope = parent.AdoptCase(new ContextEngineScope(graph, @case), rule.CaseTag);
            var program = rule.Program;
            var tuple = (uint[])request.Payload!;
            var element = program.ElementAt(program.Columns[program.SetColumn], tuple[program.SetColumn], caseIndex);
            var facts = program.Template.Instantiate(tuple, element, parent);
            scope.Engine.EnsureClean();

            // The assumptions' classes come from the scope the split was made in, which the run holds until it ends (its
            // own scope, or one of its cases), so they are not refused if that scope is disposed meanwhile.
            var batch = new HostBatch(scope, parent.Origin);
            foreach (var fact in facts)
            {
                try
                {
                    batch.PlanFact(fact);
                }
                catch (Exception refused) when (refused is FactConflictException or ArgumentException)
                {
                    // The run writes the assumption, so its conflict is a run's write conflict: an evaluation failure, which
                    // Run turns into a fault of its scope as well. Nothing of the batch is written.
                    var failure = new RuleEvaluationException(rule.Name, null,
                        $"split '{rule.Name}': case {caseIndex} of '{parent.Path}' assumes {fact}, which cannot be written: {refused.Message}", refused);
                    scope.Fault(failure);
                    throw failure;
                }
            }

            if (batch.IsEmpty == false)
            {
                scope.Runtime.TakeFailure();
                try
                {
                    batch.Commit();
                }
                catch (Relations.PanicException panic)
                {
                    throw scope.FaultWith(panic);
                }
            }

            batch.Origin.Committed = true;
        }

        /// <summary>
        /// Gets the scope of an engine context of this graph: its runtime's owner.
        /// </summary>
        Scope OwnerOf(Relations.EGraphContext context) =>
            context.GetLocal(graph.RuntimeSlot).Owner ?? throw new InvalidOperationException("A split's scope has no rule-layer scope.");

    }

}
