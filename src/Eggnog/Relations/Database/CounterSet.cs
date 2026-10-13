using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The database's counters, by <see cref="CounterId"/>: egglog's <c>Counters</c>
    /// (core-relations/src/free_join/mod.rs:261-284), a dense map of atomic integers that rules and merges use to mint
    /// fresh ids and that the bridge uses as its timestamp clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counters are global to the store: every execution state and every context reads and advances the same ones.
    /// <see cref="Increment"/> is egglog's <c>fetch_add(1, Release)</c> and returns the value before the
    /// increment; <see cref="Read"/> is an <c>Acquire</c> load. Here the increment is an interlocked add (a full fence)
    /// and the read a volatile load.
    /// </para>
    /// <para>
    /// Threading: <see cref="Increment"/>, <see cref="IncrementBy"/> and <see cref="Read"/> may run on any number of
    /// threads at once. <see cref="Add"/> and <see cref="Clone"/> need exclusive access (egglog's
    /// <c>&amp;mut Database</c> and <c>Clone</c>), because adding may move the storage; a <see cref="Store"/> therefore
    /// refuses new counters once it has had a child context, whose threads may increment concurrently.
    /// </para>
    /// </remarks>
    internal sealed class CounterSet
    {

        long[] values;
        int count;

        /// <summary>
        /// Initializes an empty set.
        /// </summary>
        public CounterSet()
        {
            values = Array.Empty<long>();
        }

        /// <summary>
        /// Initializes a copy holding <paramref name="values"/>, for <see cref="Clone"/>.
        /// </summary>
        /// <param name="values">The counters' values, which the copy takes over.</param>
        /// <param name="count">The number of counters.</param>
        CounterSet(long[] values, int count)
        {
            this.values = values;
            this.count = count;
        }

        /// <summary>
        /// Gets the number of counters.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Adds a counter starting at zero. egglog's <c>add_counter</c>. Requires exclusive access. Amortized O(1).
        /// </summary>
        /// <returns>The new counter's id, the next unused one.</returns>
        public CounterId Add()
        {
            if (count == values.Length)
                Array.Resize(ref values, Math.Max(4, values.Length * 2));

            values[count] = 0;
            return new CounterId(count++);
        }

        /// <summary>
        /// Increments <paramref name="counter"/> and returns its previous value. egglog's <c>inc</c>. Thread-safe, O(1).
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value before the increment.</returns>
        /// <exception cref="KeyNotFoundException">No such counter.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Increment(CounterId counter)
        {
            var index = counter.Index;
            if ((uint)index >= (uint)count)
                ThrowUnknown(counter);

            return Interlocked.Increment(ref values[index]) - 1;
        }

        /// <summary>
        /// Advances <paramref name="counter"/> by <paramref name="amount"/> and returns its previous value: reserves the
        /// <paramref name="amount"/> consecutive values starting there in one atomic step. Child contexts reserve their
        /// id blocks this way (<see cref="IdBlockAllocator"/>). Thread-safe, O(1).
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <param name="amount">How many values to reserve; not negative.</param>
        /// <returns>The value before the advance: the first reserved value.</returns>
        /// <exception cref="KeyNotFoundException">No such counter.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative.</exception>
        public long IncrementBy(CounterId counter, long amount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(amount);
            var index = counter.Index;
            if ((uint)index >= (uint)count)
                ThrowUnknown(counter);

            return Interlocked.Add(ref values[index], amount) - amount;
        }

        /// <summary>
        /// Gets the current value of <paramref name="counter"/>. egglog's <c>read</c>. Thread-safe, O(1).
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The value.</returns>
        /// <exception cref="KeyNotFoundException">No such counter.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Read(CounterId counter)
        {
            var index = counter.Index;
            if ((uint)index >= (uint)count)
                ThrowUnknown(counter);

            return Volatile.Read(ref values[index]);
        }

        /// <summary>
        /// Creates an independent copy holding a snapshot of every counter. egglog's <c>Clone for Counters</c>.
        /// Requires that no thread increments meanwhile for the snapshot to be consistent.
        /// </summary>
        /// <returns>The copy.</returns>
        public CounterSet Clone()
        {
            var copy = new long[values.Length];
            for (var i = 0; i < count; i++)
                copy[i] = Volatile.Read(ref values[i]);

            return new CounterSet(copy, count);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowUnknown(CounterId counter)
        {
            throw new KeyNotFoundException($"No counter {counter}.");
        }

    }

}
