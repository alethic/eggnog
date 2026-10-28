using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Eggnog
{

    /// <summary>
    /// The sync of mirrors just before use: before an iteration, a query or a freeze reads <c>T @tag</c> in a
    /// scope, the tagged scope runs the tag's copy rules once if it is writable and changed since its last sync.
    /// </summary>
    /// <remarks>
    /// The tagged scope is the reading scope or a writable ancestor of it, so it shares the reading scope's thread domain,
    /// which the caller holds; nothing else can change it until the caller's engine work ends. A frozen tagged scope was
    /// synced when it froze and never changes. A copy rule reads no mirror, so one iteration brings a mirror current.
    /// </remarks>
    internal static class MirrorSync
    {

        /// <summary>
        /// For each tag, brings <c>resolve(scope, tag)</c>'s mirrors current if that scope is writable and its view changed
        /// since its last sync (the view stamp of it and its ancestors moved, or the tag gained a copy rule); records a
        /// <see cref="RunStepKind.MirrorSync"/> step when <paramref name="executor"/> is given. Every tag is resolved before
        /// any sync, so an unresolved tag is refused before any work. Syncs are not iterations of the run, and
        /// their stamp moves are left out of the tagged scope's <see cref="Scope.Version"/>: mirror tables are internal.
        /// </summary>
        /// <param name="scope">The reading scope.</param>
        /// <param name="tags">The tags read.</param>
        /// <param name="reader">What reads them, for the resolution message: <c>Rule 'x'</c>, <c>Query 'q'</c>.</param>
        /// <param name="executor">The run, when the sync precedes one of its iterations; <c>null</c> otherwise.</param>
        /// <exception cref="InvalidOperationException">A tag does not resolve from <paramref name="scope"/>; nothing was
        /// synced.</exception>
        /// <exception cref="RuleEvaluationException">A copy rule's merge failed; the tagged scope is faulted.</exception>
        public static void Ensure(Scope scope, IReadOnlyList<ScopeTag> tags, string reader, ScheduleExecutor? executor)
        {
            foreach (var tag in tags)
                ScopeResolver.Resolve(scope, tag, reader);

            foreach (var tag in tags)
            {
                var tagged = ScopeResolver.Resolve(scope, tag, reader);
                if (tagged.IsFrozen)
                    continue;

                var rules = scope.Graph.Mirrors.CopyRules(tag);
                if (rules.Length == 0 || tagged.IsMirrorCurrent(tag, rules.Length))
                    continue;

                var started = Stopwatch.GetTimestamp();
                var engine = tagged.Engine;
                engine.EnsureClean();
                var lowered = engine.PrepareRules(rules, true);
                var ids = new Relations.EGraphRuleId[lowered.Length];
                for (var i = 0; i < ids.Length; i++)
                    ids[i] = lowered[i].Id;

                tagged.Runtime.TakeFailure();
                var before = engine.Version;
                EngineIteration iteration;
                try
                {
                    iteration = engine.RunRules(ids);
                }
                catch (Relations.PanicException panic)
                {
                    throw tagged.FaultWith(panic);
                }

                tagged.ExcludeMirrorSync(engine.Version - before);
                tagged.MarkMirrorSynced(tag, rules.Length);
                executor?.RecordStep(RunStepKind.MirrorSync, tagged, iteration.Changed || iteration.RebuildChanged, Stopwatch.GetElapsedTime(started), null);
            }
        }

    }

}
