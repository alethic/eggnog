namespace Eggnog.Relations
{

    /// <summary>
    /// Says how an <see cref="ObjectPool{T, TPolicy}"/> creates, clears, admits and weighs objects of type
    /// <typeparamref name="T"/>. The C# form of egglog's <c>Clear</c> trait (core-relations/src/pool/mod.rs:35-46),
    /// written as static members so that a pool specialized over a struct policy calls them directly, with no
    /// interface dispatch.
    /// </summary>
    /// <remarks>
    /// Implementations are stateless structs. Rust implements <c>Clear</c> on the pooled type itself; a policy type
    /// lets the engine pool types it does not own, such as <see cref="System.Collections.Generic.List{T}"/>, which is
    /// what egglog does with its <c>impl Clear for Vec&lt;T&gt;</c> and the hash containers.
    /// </remarks>
    /// <typeparam name="T">The pooled type.</typeparam>
    internal interface IPoolPolicy<T>
        where T : class
    {

        /// <summary>
        /// Creates an empty object: what a pool hands out when it holds nothing. Rust's <c>Default::default</c>.
        /// </summary>
        /// <returns>A new, empty object.</returns>
        static abstract T Create();

        /// <summary>
        /// Clears <paramref name="item"/> so that it is equivalent to a freshly created object, keeping whatever
        /// allocation makes it worth reusing.
        /// </summary>
        /// <param name="item">The object to clear.</param>
        static abstract void Clear(T item);

        /// <summary>
        /// Gets whether <paramref name="item"/> is worth keeping in a pool. egglog keeps a <c>Vec</c> only when its
        /// capacity exceeds 256 elements and a hash container only when it has any capacity at all: small objects are
        /// cheaper to allocate again than to pool.
        /// </summary>
        /// <param name="item">The object.</param>
        /// <returns><c>true</c> to keep it.</returns>
        static virtual bool ShouldReuse(T item) => true;

        /// <summary>
        /// Estimates the memory <paramref name="item"/> keeps alive, in bytes. Pools charge it against their byte
        /// limit. Must not change while the object sits in a pool, and must not be negative.
        /// </summary>
        /// <param name="item">The object.</param>
        /// <returns>The estimate.</returns>
        static abstract long Bytes(T item);

        /// <summary>
        /// Gets the per-thread byte limit of the engine-wide pool for this policy, <see cref="SharedPool{T, TPolicy}"/>:
        /// the <c>[bytes]</c> egglog gives the type in its <c>pool_set!</c> declaration
        /// (core-relations/src/pool/mod.rs:424-446; see <see cref="PoolLimits"/>). Use <see cref="long.MaxValue"/> for
        /// no limit, which is egglog's <c>Pool::default</c>.
        /// </summary>
        static abstract long LimitBytes { get; }

    }

}
