using System;
using System.Collections.Generic;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// A scheduler added to a graph (<see cref="EGraph.AddScheduler"/>): egglog's <c>SchedulerId</c>, which
    /// <see cref="Schedule.RunWith"/> steps rulesets with. Renders as <c>scheduler0</c>, <c>scheduler1</c>, …, in the
    /// order added.
    /// </summary>
    public sealed class RuleScheduler
    {

        readonly Dictionary<CompiledRule, ScheduledRule> rules = new(ReferenceEqualityComparer.Instance);
        volatile bool removed;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph.</param>
        /// <param name="prototype">The scheduler each scope steps a clone of.</param>
        /// <param name="id">Its number in the graph.</param>
        internal RuleScheduler(EGraph graph, IRuleScheduler prototype, int id)
        {
            Graph = graph;
            Prototype = prototype;
            Name = "scheduler" + id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Gets the graph the scheduler was added to.
        /// </summary>
        public EGraph Graph { get; }

        /// <summary>
        /// Gets the scheduler as added: the prototype each scope that steps clones, never called itself.
        /// </summary>
        public IRuleScheduler Prototype { get; }

        /// <summary>
        /// Gets the name it renders as.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets whether it was removed (<see cref="EGraph.RemoveScheduler"/>); a run that would step it is refused.
        /// </summary>
        public bool IsRemoved => removed;

        /// <inheritdoc />
        public override string ToString() => Name;

        /// <summary>
        /// Gets the number of rules it has scheduled forms of, for tests.
        /// </summary>
        internal int ScheduledRuleCount
        {
            get
            {
                lock (Graph.DefinitionLock)
                    return rules.Count;
            }
        }

        /// <summary>
        /// Gets the scheduled form of a rule, building it at its first step: its query and action rules and its worklist.
        /// </summary>
        /// <param name="compiled">The rule.</param>
        /// <returns>The scheduled rule.</returns>
        /// <exception cref="ArgumentException">The head uses no variable of the body.</exception>
        /// <exception cref="InvalidOperationException">The scheduler was removed, or another thread is using the writable
        /// root.</exception>
        internal ScheduledRule Schedule(CompiledRule compiled)
        {
            lock (Graph.DefinitionLock)
            {
                if (removed)
                    throw new InvalidOperationException($"Scheduler {Name} was removed.");

                if (rules.TryGetValue(compiled, out var scheduled))
                    return scheduled;

                scheduled = ScheduledRule.Create(Graph, this, compiled, Graph.NextSchedulerSlot());
                rules.Add(compiled, scheduled);
                return scheduled;
            }
        }

        /// <summary>
        /// Gets the default variants of the scheduled rules, for a freeze to plan. Called under the graph's definition
        /// lock.
        /// </summary>
        /// <param name="add">Receives each variant.</param>
        internal void AddPlanRules(Action<LoweredRule?> add)
        {
            foreach (var scheduled in rules.Values)
            {
                add(scheduled.Query.Default);
                add(scheduled.Action.Default);
            }
        }

        /// <summary>
        /// Frees the scheduled form of a local rule whose scope was released: no scope can step it again. Its worklist goes
        /// back to the graph for another rule of the same columns. Called under the graph's definition lock.
        /// </summary>
        /// <param name="compiled">The rule.</param>
        internal void Forget(CompiledRule compiled)
        {
            if (rules.Remove(compiled, out var scheduled))
                Release(scheduled, deferred: false);
        }

        /// <summary>
        /// Marks the scheduler removed and frees its rules' variants before the graph's next engine call; their worklists
        /// go back to the graph. Called under the graph's definition lock.
        /// </summary>
        /// <returns>Whether it was not removed before.</returns>
        internal bool Remove()
        {
            if (removed)
                return false;

            removed = true;
            foreach (var scheduled in rules.Values)
                Release(scheduled, deferred: true);

            rules.Clear();
            return true;
        }

        /// <summary>
        /// Frees a scheduled rule's variants, now or before the next engine call, and returns its worklist, which every
        /// step leaves empty, to the graph.
        /// </summary>
        void Release(ScheduledRule scheduled, bool deferred)
        {
            if (deferred)
            {
                Graph.DeferFree(scheduled.Query);
                Graph.DeferFree(scheduled.Action);
            }
            else
            {
                scheduled.Query.Free(Graph);
                scheduled.Action.Free(Graph);
            }

            Graph.ReturnWorklist(scheduled.Worklist);
        }

    }

}
