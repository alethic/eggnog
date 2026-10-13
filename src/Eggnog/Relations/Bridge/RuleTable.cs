using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The rules of an <see cref="EGraph"/>, by id: egglog-bridge's <c>rules</c>, a <c>DenseIdMapWithReuse</c>
    /// (egglog-bridge/src/lib.rs:120), made safe for definitions from any thread (colored-contexts spec §10.2, X16;
    /// rule-layer D6: rules are never sealed and may be defined while other threads run).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An id is reserved when a builder starts (<see cref="Reserve"/>) and filled when it builds (<see cref="Publish"/>);
    /// a freed id is handed out again, last freed first, as egglog's <c>reserve_slot</c> does. Each reservation of a slot
    /// gets the next <b>generation</b> of that slot (starting at 1), which a <see cref="RuleInfo"/> carries and every
    /// context's <see cref="RuleProgress"/> records: progress of a freed rule never applies to the rule that reuses its id
    /// (§13 edge 29).
    /// </para>
    /// <para>
    /// Threading: writers serialize on a lock; readers (<see cref="TryGet"/>, run on any thread, also by the rule tasks of
    /// concurrent children) are lock-free. The slot array is copied and published whole when it grows (copy-on-write), and
    /// a slot is written with a volatile write, so a reader sees either a complete rule or none. Deviation from the spec's
    /// "copy-on-write" table: a definition copies the array only when it grows, not on every change, so defining n rules
    /// costs O(n) and not O(n²) (the rule layer defines a rule per query, D49).
    /// </para>
    /// </remarks>
    internal sealed class RuleTable
    {

        readonly object gate = new();
        readonly List<int> free = new();
        RuleInfo?[] slots = Array.Empty<RuleInfo?>();
        int[] generations = Array.Empty<int>();
        bool[] reserved = Array.Empty<bool>();
        int next;
        int count;

        /// <summary>
        /// Gets the number of built rules.
        /// </summary>
        public int Count => Volatile.Read(ref count);

        /// <summary>
        /// Gets one more than the largest id ever handed out: every rule's id is below it.
        /// </summary>
        public int Capacity => Volatile.Read(ref next);

        /// <summary>
        /// Reserves the id of a new rule: the most recently freed one, or the next unused one. egglog's
        /// <c>reserve_slot</c> (numeric-id/src/lib.rs:322-331). O(1) amortized.
        /// </summary>
        /// <returns>The id and the generation the rule built in it gets.</returns>
        public (EGraphRuleId Id, int Generation) Reserve()
        {
            lock (gate)
            {
                int index;
                if (free.Count > 0)
                {
                    index = free[^1];
                    free.RemoveAt(free.Count - 1);
                }
                else
                {
                    index = next;
                    EnsureCapacityLocked(index + 1);
                    Volatile.Write(ref next, index + 1);
                }

                reserved[index] = true;
                var generation = ++generations[index];
                return (new EGraphRuleId(index), generation);
            }
        }

        /// <summary>
        /// Fills a reserved id with its rule: the rule takes part in every run that lists it from now on.
        /// </summary>
        /// <param name="id">The reserved id.</param>
        /// <param name="info">The rule; its <see cref="RuleInfo.Generation"/> must be the reservation's.</param>
        /// <exception cref="InvalidOperationException">The id is not reserved, holds a rule already, or was reserved with
        /// another generation.</exception>
        public void Publish(EGraphRuleId id, RuleInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            lock (gate)
            {
                var index = id.Index;
                if ((uint)index >= (uint)next || reserved[index] == false)
                    throw new InvalidOperationException($"Rule id {id} is not reserved.");

                if (slots[index] is not null)
                    throw new InvalidOperationException($"Rule id {id} holds a rule already.");

                if (generations[index] != info.Generation)
                    throw new InvalidOperationException($"Rule id {id} was reserved for generation {generations[index]}, not {info.Generation}.");

                Volatile.Write(ref slots[index], info);
                Volatile.Write(ref count, count + 1);
            }
        }

        /// <summary>
        /// Reserves an id and fills it with the rule <paramref name="create"/> makes for it and its generation.
        /// </summary>
        /// <param name="create">Makes the rule; called once, under the table's lock.</param>
        /// <returns>The rule's id.</returns>
        public EGraphRuleId Add(Func<EGraphRuleId, int, RuleInfo> create)
        {
            ArgumentNullException.ThrowIfNull(create);
            lock (gate)
            {
                var (id, generation) = Reserve();
                Publish(id, create(id, generation));
                return id;
            }
        }

        /// <summary>
        /// Removes a built rule; its id may be handed out again, with the next generation. An id that is reserved and not
        /// built, or unknown, is left alone, as egglog's <c>free_rule</c> leaves it.
        /// </summary>
        /// <param name="id">The rule's id.</param>
        /// <param name="info">The removed rule.</param>
        /// <returns><c>true</c> if a built rule was removed.</returns>
        public bool Remove(EGraphRuleId id, [MaybeNullWhen(false)] out RuleInfo info)
        {
            lock (gate)
            {
                var index = id.Index;
                if ((uint)index >= (uint)next || slots[index] is not { } found)
                {
                    info = null;
                    return false;
                }

                Volatile.Write(ref slots[index], null);
                reserved[index] = false;
                free.Add(index);
                Volatile.Write(ref count, count - 1);
                info = found;
                return true;
            }
        }

        /// <summary>
        /// Gets the built rule with id <paramref name="id"/>. Lock-free; any thread.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="info">The rule.</param>
        /// <returns><c>false</c> if no built rule has the id.</returns>
        public bool TryGet(EGraphRuleId id, [MaybeNullWhen(false)] out RuleInfo info)
        {
            var current = Volatile.Read(ref slots);
            var index = id.Index;
            if ((uint)index < (uint)current.Length && Volatile.Read(ref current[index]) is { } found)
            {
                info = found;
                return true;
            }

            info = null;
            return false;
        }

        /// <summary>
        /// Adds every built rule to <paramref name="into"/>, in id order: a snapshot (§10.8 step 4, the invariant checker).
        /// O(capacity).
        /// </summary>
        /// <param name="into">Receives the rules.</param>
        public void CopyLive(List<RuleInfo> into)
        {
            ArgumentNullException.ThrowIfNull(into);
            var current = Volatile.Read(ref slots);
            for (var i = 0; i < current.Length; i++)
                if (Volatile.Read(ref current[i]) is { } info)
                    into.Add(info);
        }

        /// <summary>
        /// Grows the arrays to hold <paramref name="length"/> slots, copying and publishing the slot array whole. Under
        /// the lock.
        /// </summary>
        void EnsureCapacityLocked(int length)
        {
            if (length <= slots.Length)
                return;

            var size = Math.Max(length, Math.Max(8, slots.Length * 2));
            var grown = new RuleInfo?[size];
            Array.Copy(slots, grown, slots.Length);
            Array.Resize(ref generations, size);
            Array.Resize(ref reserved, size);
            Volatile.Write(ref slots, grown);
        }

    }

}
