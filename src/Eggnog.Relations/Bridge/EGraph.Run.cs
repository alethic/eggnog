using System;
using System.Diagnostics;

namespace Eggnog.Relations
{

    internal sealed partial class EGraph
    {

        /// <summary>
        /// Merges every staged write, advances the timestamp, and rebuilds if the merge unioned anything. egglog's
        /// <c>flush_updates</c> (lib.rs:946-959).
        /// </summary>
        /// <remarks>
        /// The merge runs at the current timestamp: rows and unions merge functions write carry it. A panic recorded by a
        /// merge (an <see cref="MergeFn.AssertEq"/> conflict, say) is not thrown here; the next run reports it, as in
        /// egglog.
        /// </remarks>
        /// <returns>Whether the merge changed the database; changes made by the rebuild are not reported.</returns>
        public bool FlushUpdates()
        {
            var unionsBefore = UnionCount;
            var updated = db.MergeAll(NextTimestamp);
            IncrementTimestamp();
            if (UnionCount != unionsBefore)
                Rebuild();

            return updated;
        }

        /// <summary>
        /// Runs one iteration: <paramref name="runRuleSet"/> at the current timestamp, then either advancing the timestamp
        /// (no effective union) or the rebuild loop, then the panic check. egglog's
        /// <c>run_rules_inner</c> (lib.rs:591-624), with the rule set's execution supplied by the caller (the rule
        /// layer's cached plans and seminaive variants, or a core <see cref="RuleSet"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="runRuleSet"/> receives the run's timestamp <c>T</c>, which every row the run writes must carry
        /// and which rules record as their last run; the counter does not move while it runs. Afterwards, if the union-find
        /// recorded no new union, no row can have become non-canonical: the counter advances to <c>T + 1</c> and the
        /// iteration ends. Otherwise the rebuild loop runs, its first pass at <c>T</c> (lib.rs:667), so rebuilt rows count
        /// as new for the rules just run.
        /// </para>
        /// <para>
        /// A panic recorded during the rule set or the rebuild is thrown at the end, with the writes made before the stop
        /// merged (map §3.9 item 8). Defect fixed (egglog-bridge/src/lib.rs:597-599; map §8.1 row 22): egglog returns the
        /// error right after the rule set, before advancing the timestamp or rebuilding, so unions the run merged leave
        /// the tables non-canonical for whatever runs next (the frontend's <c>fail</c> command continues after such an
        /// error), and the next run reuses the timestamp. Here the iteration finishes its bookkeeping first and then
        /// throws the first panic; a panic the rebuild records afterwards belongs to the same failed iteration and is
        /// dropped.
        /// </para>
        /// </remarks>
        /// <param name="runRuleSet">Runs the rule set against the database at the given timestamp and merges its writes.</param>
        /// <returns>The report.</returns>
        /// <exception cref="PanicException">A panic was recorded.</exception>
        internal IterationReport RunIteration(Func<uint, RuleSetReport> runRuleSet)
        {
            ArgumentNullException.ThrowIfNull(runRuleSet);

            var timestamp = NextTimestamp;
            var unionsBefore = UnionCount;
            var report = runRuleSet(timestamp) ?? throw new InvalidOperationException("The rule set runner returned no report.");
            var panicked = TryTakePanic(out var message);

            if (UnionCount == unionsBefore)
            {
                // No new unions: skip the rebuild but still advance the timestamp, so seminaive evaluation sees a fresh
                // epoch (lib.rs:606-613).
                IncrementTimestamp();
                if (panicked)
                    throw new PanicException(message);

                return new IterationReport(report, TimeSpan.Zero, false);
            }

            var start = Stopwatch.GetTimestamp();
            var rebuildChanged = Rebuild();
            var rebuildTime = Stopwatch.GetElapsedTime(start);
            if (panicked)
            {
                TryTakePanic(out _);
                throw new PanicException(message);
            }

            ThrowIfPanicked();
            return new IterationReport(report, rebuildTime, rebuildChanged);
        }

        /// <summary>
        /// Runs a core rule set as one iteration (<see cref="RunIteration"/>): the rules run at the current timestamp
        /// (<see cref="ExecutionState.Timestamp"/>; actions that stamp rows read the timestamp counter, which holds the
        /// same value) and their writes merge, then the e-graph rebuilds if they unioned anything.
        /// </summary>
        /// <param name="ruleSet">The rules, built against the database as it is now.</param>
        /// <returns>The report.</returns>
        /// <exception cref="PanicException">A panic was recorded.</exception>
        internal IterationReport RunRuleSet(RuleSet ruleSet)
        {
            ArgumentNullException.ThrowIfNull(ruleSet);
            return RunIteration(timestamp => db.RunRuleSet(ruleSet, ReportLevel, timestamp));
        }

        /// <summary>
        /// Rebuilds to a fixpoint: each pass rebuilds the containers, then every function table against the union-find
        /// (merging, so collisions union their outputs), then re-stamps rows mentioning containers whose meaning changed
        /// in place, all at the pass's timestamp, which advances after it; passes repeat until one changes nothing.
        /// egglog's native <c>rebuild</c> loop (lib.rs:626-682, through <see cref="Database.Rebuild"/>); egglog's
        /// rule-based fallback is unreachable and not ported (decision 10).
        /// </summary>
        /// <returns>Whether any pass changed a table or container.</returns>
        internal bool Rebuild() => db.Rebuild(ufTable, FunctionTables, timestampCounter) > 1;

        /// <summary>
        /// Advances the timestamp counter. egglog's <c>inc_ts</c>.
        /// </summary>
        internal void IncrementTimestamp() => db.IncrementCounter(timestampCounter);

    }

}
