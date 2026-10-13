using System;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// The smallest capacity of the progress map once it holds anything.
        /// </summary>
        const int MinProgressCapacity = 8;

        /// <summary>
        /// The keys of the context's progress map: a rule's index plus one, 0 for a free slot. Open addressing with
        /// linear probing, a power-of-two capacity, at most half full; parallel to <see cref="progressValues"/>.
        /// </summary>
        /// <remarks>
        /// A map rather than an array indexed by rule id, so a context pays for the rules it ran, not for every rule
        /// the e-graph defines (the rule layer defines one rule per query, and a child that ran a high-index rule would
        /// otherwise allocate a slot per lower index). Written on the domain thread only, both arrays replaced together
        /// when they grow; read by descendants, which share the domain or read it frozen (immutable since the freeze).
        /// </remarks>
        int[] progressKeys = Array.Empty<int>();

        /// <summary>
        /// The values of the progress map, parallel to <see cref="progressKeys"/>; a slot whose generation differs from
        /// the rule's holds nothing for it.
        /// </summary>
        RuleProgress[] progressValues = Array.Empty<RuleProgress>();

        /// <summary>
        /// The number of rules the progress map holds.
        /// </summary>
        int progressCount;

        /// <summary>
        /// Gets rule <paramref name="rule"/>'s watermark in this context for a run whose executed constants are
        /// <paramref name="constants"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The context's own progress decides when it has any for the rule's generation: its last run's timestamp, or 0
        /// (a full run) when the constants differ. Otherwise, for a rule that may inherit
        /// (<see cref="RuleInfo.AllowInheritance"/>) in a context that inherits
        /// (<see cref="ContextOptions.InheritWatermarks"/>), the watermark is seeded lazily from the nearest ancestor
        /// with progress: <c>min(W_a, CreatedAt of the ancestor's child on the path)</c>, or 0 when the constants
        /// differ. So an inherited watermark is at most this context's <see cref="CreatedAt"/>, which its semi-naive
        /// floor relies on, and it is sound: every visible row older than it lies in the ancestor's layer or above and
        /// was seen, unchanged, by the ancestor's run.
        /// </para>
        /// <para>
        /// O(depth). Reading an ancestor's progress is safe: the ancestor is frozen (immutable) or shares this domain.
        /// </para>
        /// </remarks>
        /// <param name="rule">The rule's id.</param>
        /// <param name="info">The rule.</param>
        /// <param name="constants">K for this run, or <c>null</c> when the rule has no Id constants.</param>
        /// <returns>The watermark: rows stamped at or after it are new to the rule's run here.</returns>
        internal uint GetWatermark(EGraphRuleId rule, RuleInfo info, uint[]? constants)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (TryOwn(rule, info.Generation, out var own))
                return RuleProgress.SameConstants(own.Constants, constants) ? own.LastRunAt : 0;

            if (parent is null || info.AllowInheritance == false || context.Options.InheritWatermarks == false)
                return 0;

            var bound = uint.MaxValue;
            for (var c = this; c.parent is { } up; c = up)
            {
                // The CreatedAt of the ancestor's child on the path: rows it or its descendants wrote are at or above it.
                bound = Math.Min(bound, c.CreatedAt);
                if (up.TryOwn(rule, info.Generation, out var inherited))
                    return RuleProgress.SameConstants(inherited.Constants, constants) ? Math.Min(inherited.LastRunAt, bound) : 0;
            }

            return 0;
        }

        /// <summary>
        /// Gets the timestamp of rule <paramref name="rule"/>'s last committed run in this context, 0 when it has none for
        /// <paramref name="generation"/>: its own progress, never an inherited one.
        /// </summary>
        /// <param name="rule">The rule's id.</param>
        /// <param name="generation">The rule's generation.</param>
        /// <returns>The timestamp, or 0.</returns>
        internal uint CommittedLastRunAt(EGraphRuleId rule, int generation) => TryOwn(rule, generation, out var p) ? p.LastRunAt : 0;

        /// <summary>
        /// Gets this context's own progress for rule <paramref name="rule"/> of generation <paramref name="generation"/>.
        /// </summary>
        /// <param name="rule">The rule's id.</param>
        /// <param name="generation">The rule's generation.</param>
        /// <param name="value">The progress.</param>
        /// <returns><c>false</c> when the context holds no progress for the rule, or holds another generation's.
        /// O(1) expected.</returns>
        internal bool TryOwn(EGraphRuleId rule, int generation, out RuleProgress value)
        {
            var slot = FindProgressSlot(progressKeys, rule.Index);
            if (slot >= 0 && progressValues[slot].Generation == generation)
            {
                value = progressValues[slot];
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Sets this context's own progress for <paramref name="rule"/>: the commit of one rule's run, and the
        /// checker tests' way to corrupt it. Domain thread only. O(1) amortized; the map doubles when half full.
        /// </summary>
        /// <param name="rule">The rule's id.</param>
        /// <param name="value">The progress.</param>
        internal void SetProgress(EGraphRuleId rule, RuleProgress value)
        {
            var keys = progressKeys;
            var slot = FindProgressSlot(keys, rule.Index);
            if (slot < 0)
            {
                if (2 * (progressCount + 1) > keys.Length)
                {
                    GrowProgress();
                    keys = progressKeys;
                }

                slot = ProbeFree(keys, rule.Index);
                keys[slot] = rule.Index + 1;
                progressCount++;
            }

            progressValues[slot] = value;
        }

        /// <summary>
        /// Commits the progress a run staged, in listing order, so a rule listed twice keeps its last staging.
        /// Domain thread only; O(rules run).
        /// </summary>
        void Commit(ReadOnlySpan<EGraphRuleId> rules, RuleProgress[] staged)
        {
            for (var i = 0; i < rules.Length; i++)
                SetProgress(rules[i], staged[i]);
        }

        /// <summary>
        /// Gets the slot holding rule index <paramref name="index"/> in <paramref name="keys"/>, or -1. O(1) expected.
        /// </summary>
        static int FindProgressSlot(int[] keys, int index)
        {
            if (keys.Length == 0)
                return -1;

            var mask = keys.Length - 1;
            var key = index + 1;
            for (var slot = ProgressHash(index) & mask; ; slot = (slot + 1) & mask)
            {
                var k = keys[slot];
                if (k == key)
                    return slot;

                if (k == 0)
                    return -1;
            }
        }

        /// <summary>
        /// Gets the first free slot on rule index <paramref name="index"/>'s probe sequence; the map has one.
        /// </summary>
        static int ProbeFree(int[] keys, int index)
        {
            var mask = keys.Length - 1;
            var slot = ProgressHash(index) & mask;
            while (keys[slot] != 0)
                slot = (slot + 1) & mask;

            return slot;
        }

        /// <summary>
        /// Spreads a rule index over the map (Fibonacci hashing, folded).
        /// </summary>
        static int ProgressHash(int index)
        {
            var h = (uint)index * 0x9E3779B9u;
            return (int)(h ^ (h >> 16));
        }

        /// <summary>
        /// Doubles the progress map (to <see cref="MinProgressCapacity"/> from empty) and rehashes it; the new arrays are
        /// published together, keys last.
        /// </summary>
        void GrowProgress()
        {
            var oldKeys = progressKeys;
            var oldValues = progressValues;
            var capacity = Math.Max(MinProgressCapacity, oldKeys.Length * 2);
            var keys = new int[capacity];
            var values = new RuleProgress[capacity];
            for (var i = 0; i < oldKeys.Length; i++)
            {
                if (oldKeys[i] == 0)
                    continue;

                var slot = ProbeFree(keys, oldKeys[i] - 1);
                keys[slot] = oldKeys[i];
                values[slot] = oldValues[i];
            }

            progressValues = values;
            Volatile.Write(ref progressKeys, keys);
        }

        /// <summary>
        /// Drops the context's progress: it is disposed.
        /// </summary>
        void ReleaseProgress()
        {
            progressCount = 0;
            progressValues = Array.Empty<RuleProgress>();
            Volatile.Write(ref progressKeys, Array.Empty<int>());
        }

    }

}
