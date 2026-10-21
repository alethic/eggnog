using System;
using System.Collections.Generic;
using System.Threading;

namespace Eggnog
{

    public sealed partial class EGraph
    {

        readonly List<RuleScheduler> schedulers = new();
        readonly Dictionary<string, Stack<TableHandle>> freeWorklists = new(StringComparer.Ordinal);
        int schedulerCount;
        int schedulerSlots;
        int worklistCount;

        /// <summary>
        /// Adds a scheduler: egglog's <c>add_scheduler</c>. <see cref="Schedule.RunWith"/> steps rulesets with it; each
        /// scope that steps it steps its own clone of <paramref name="scheduler"/>, which is never called itself.
        /// </summary>
        /// <param name="scheduler">The scheduler.</param>
        /// <returns>Its handle.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="scheduler"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">The call comes from a primitive, lattice, filter, cost function or
        /// scheduler.</exception>
        public RuleScheduler AddScheduler(IRuleScheduler scheduler)
        {
            EngineCallMarker.ThrowIfActive();
            ArgumentNullException.ThrowIfNull(scheduler);
            lock (DefinitionLock)
            {
                var added = new RuleScheduler(this, scheduler, schedulerCount++);
                schedulers.Add(added);
                return added;
            }
        }

        /// <summary>
        /// Removes a scheduler: egglog's <c>remove_scheduler</c>. A run that would step it is refused after; remove it only
        /// when no run is stepping it. Its rules are freed; the states scopes hold of it are never used again.
        /// </summary>
        /// <param name="scheduler">The scheduler.</param>
        /// <returns>Whether it was not removed before.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="scheduler"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The scheduler belongs to another graph.</exception>
        /// <exception cref="InvalidOperationException">The call comes from a primitive, lattice, filter, cost function or
        /// scheduler.</exception>
        public bool RemoveScheduler(RuleScheduler scheduler)
        {
            EngineCallMarker.ThrowIfActive();
            ArgumentNullException.ThrowIfNull(scheduler);
            if (ReferenceEquals(scheduler.Graph, this) == false)
                throw new ArgumentException($"Scheduler {scheduler.Name} belongs to another graph.", nameof(scheduler));

            lock (DefinitionLock)
            {
                schedulers.Remove(scheduler);
                return scheduler.Remove();
            }
        }

        /// <summary>
        /// Frees the scheduled forms of local rules whose scope was released, from every scheduler.
        /// </summary>
        /// <param name="rules">The released rules.</param>
        internal void ForgetScheduledRules(IReadOnlyList<CompiledRule> rules)
        {
            if (rules.Count == 0)
                return;

            lock (DefinitionLock)
            {
                foreach (var scheduler in schedulers)
                    foreach (var rule in rules)
                        scheduler.Forget(rule);
            }
        }

        /// <summary>
        /// Gives every scheduled rule's default variants to a freeze to plan.
        /// </summary>
        /// <param name="add">Receives each variant.</param>
        internal void AddScheduledPlanRules(Action<LoweredRule?> add)
        {
            lock (DefinitionLock)
            {
                foreach (var scheduler in schedulers)
                    scheduler.AddPlanRules(add);
            }
        }

        /// <summary>
        /// Gets a worklist over <paramref name="keys"/>: one a freed scheduled rule left, which every step left empty, or a
        /// new one, since an engine table is never freed. Called under the definition lock.
        /// </summary>
        /// <param name="name">The name of a new worklist.</param>
        /// <param name="keys">The column types of the matches.</param>
        /// <returns>The worklist's handle.</returns>
        /// <exception cref="InvalidOperationException">Another thread is using the writable root.</exception>
        internal TableHandle TakeWorklist(string name, ColumnType[] keys)
        {
            if (freeWorklists.TryGetValue(Signature(keys), out var free) && free.Count > 0)
                return free.Pop();

            return DefineWorklistTable(name, keys, int.MinValue + worklistCount++);
        }

        /// <summary>
        /// Takes back the worklist of a freed scheduled rule, for another rule of the same columns. Called under the
        /// definition lock.
        /// </summary>
        /// <param name="worklist">The worklist.</param>
        internal void ReturnWorklist(TableHandle worklist)
        {
            var signature = Signature(worklist.KeyArray);
            if (freeWorklists.TryGetValue(signature, out var free) == false)
                freeWorklists.Add(signature, free = new Stack<TableHandle>());

            free.Push(worklist);
        }

        /// <summary>
        /// Gets a new slot for a scheduled rule's query buffer, unique in the graph.
        /// </summary>
        internal int NextSchedulerSlot() => Interlocked.Increment(ref schedulerSlots) - 1;

        /// <summary>
        /// Gets the key of a worklist's columns: their type names, unique in the graph's store.
        /// </summary>
        static string Signature(ColumnType[] keys)
        {
            var names = new string[keys.Length];
            for (var i = 0; i < keys.Length; i++)
                names[i] = keys[i].Name;

            return string.Join('\u001f', names);
        }

    }

}
