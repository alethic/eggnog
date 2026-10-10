using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// A set of pools that are released together. egglog's <c>PoolSet</c> (core-relations/src/pool/mod.rs:366-463).
    /// </summary>
    /// <remarks>
    /// <para>
    /// egglog declares its pools as the fields of one thread-local struct (the <c>pool_set!</c> macro), with
    /// <c>InPoolSet</c> picking the field for a type at compile time. In C# that lookup is
    /// <see cref="SharedPool{T, TPolicy}"/>: one pool per type and policy, created on first use with the policy's
    /// limit and registered here. The set itself only remembers pools so that <see cref="Clear"/> can release all of
    /// them, which is what egglog's <c>Database::drop</c> does on every thread. <see cref="CreatePool{T, TPolicy}"/>
    /// also makes standalone pools (tests, or a pool private to one data structure).
    /// </para>
    /// <para>
    /// Thread-safe. Registration and clearing are cold paths and take a lock.
    /// </para>
    /// </remarks>
    internal sealed class PoolSet
    {

        /// <summary>
        /// Gets the engine-wide pool set: the counterpart of egglog's thread-local <c>POOL_SET</c>.
        /// </summary>
        public static PoolSet Shared { get; } = new PoolSet();

        readonly object sync = new();
        readonly List<IPool> pools = new();

        /// <summary>
        /// Gets the number of registered pools.
        /// </summary>
        public int Count
        {
            get
            {
                lock (sync)
                    return pools.Count;
            }
        }

        /// <summary>
        /// Creates a new pool with the given per-thread byte limit and registers it. Every call makes a separate pool;
        /// code that pools a type engine-wide uses <see cref="SharedPool{T, TPolicy}"/> instead.
        /// </summary>
        /// <typeparam name="T">The pooled type.</typeparam>
        /// <typeparam name="TPolicy">The pool policy.</typeparam>
        /// <param name="limitBytes">The per-thread byte limit; see <see cref="PoolLimits"/> for egglog's values.</param>
        /// <returns>The new pool.</returns>
        public ObjectPool<T, TPolicy> CreatePool<T, TPolicy>(long limitBytes)
            where T : class
            where TPolicy : struct, IPoolPolicy<T>
        {
            var pool = new ObjectPool<T, TPolicy>(limitBytes);
            Register(pool);
            return pool;
        }

        /// <summary>
        /// Registers an existing pool so that <see cref="Clear"/> releases it too.
        /// </summary>
        /// <param name="pool">The pool.</param>
        public void Register(IPool pool)
        {
            ArgumentNullException.ThrowIfNull(pool);

            lock (sync)
                pools.Add(pool);
        }

        /// <summary>
        /// Drops every object held by every registered pool, on every thread. egglog's <c>PoolSet::clear</c>.
        /// </summary>
        public void Clear()
        {
            IPool[] snapshot;
            lock (sync)
                snapshot = pools.ToArray();

            foreach (var pool in snapshot)
                pool.Clear();
        }

    }

}
