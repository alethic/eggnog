using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// Makes the context read-only and shareable across threads (colored-contexts spec §10.8, §8.1; rule-layer E10).
        /// Its parent must be frozen already. A second call does nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Steps (§10.8): (1) flush the host writes staged here; (2) rebuild; (3) take a failure that is recorded and throw
        /// it (<see cref="PanicException"/>), leaving the context unfrozen and usable; (4) plan the rules the context's
        /// descendants will run, on this context's statistics (§9.7): with <paramref name="planRules"/> <c>null</c>, every
        /// live rule without a cached plan; otherwise exactly the given rules that have none (E10: a freeze must not plan
        /// other scopes' local rules, queries or split matchers on its statistics); (5) freeze the storage context
        /// (<see cref="Context.Freeze"/>), warming the indexes the planned rules' plans use
        /// (<see cref="FreezeWarmup.From"/>): those of every live rule's plan for <c>null</c>, those of the given rules'
        /// plans otherwise.
        /// </para>
        /// <para>
        /// Guidance (§14.2): saturate, or run every rule once after the last change, before freezing; otherwise every child
        /// re-matches the parent's last delta.
        /// </para>
        /// </remarks>
        /// <param name="planRules">The rules to plan and warm, or <c>null</c> for every live rule.</param>
        /// <exception cref="ArgumentException">A rule in <paramref name="planRules"/> is not registered.</exception>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The parent is not frozen; the context is faulted or stopped (or its
        /// rebuild stopped it); the call comes from inside another operation of the same thread domain; or the domain is
        /// held by another thread.</exception>
        /// <exception cref="PanicException">A failure was recorded (before the call, or by the flush or the
        /// rebuild).</exception>
        public void Freeze(IReadOnlyCollection<EGraphRuleId>? planRules = null)
        {
            if (context.IsFrozen)
                return;

            context.ThrowIfDisposed();
            context.ThrowIfFaulted();
            context.ThrowIfContradicted();
            ThrowIfReentrant(nameof(Freeze));
            if (parent is { IsFrozen: false })
                throw new InvalidOperationException("A context can be frozen only once its parent is.");

            // Resolve the rules before anything changes.
            var infos = new List<RuleInfo>();
            if (planRules is null)
            {
                owner.Rules.CopyLive(infos);
            }
            else
            {
                foreach (var rule in planRules)
                    infos.Add(owner.GetRuleInfo(rule));
            }

            // Steps 1-3.
            if (hostWrites.IsEmpty == false)
                FlushCore(throwOnFailure: true);

            context.Rebuild();
            context.ThrowIfContradicted();
            if (TakeFailure(out var message))
                throw new PanicException(message);

            // Step 4: plans are shared by every context and built once (decision 7), here on this context's statistics.
            var database = context.Database;
            var templates = new List<PlanTemplate>(infos.Count);
            foreach (var info in infos)
            {
                var plan = info.CachedPlan;
                if (plan is null)
                    plan = info.GetOrBuildCachedPlan(database);

                templates.Add(plan.Plan.Plan);
            }

            // Step 5.
            context.Freeze(FreezeWarmup.From(templates, database));
        }

    }

}
