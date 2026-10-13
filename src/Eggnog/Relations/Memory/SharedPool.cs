using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The engine-wide pool for objects of type <typeparamref name="T"/> handled by <typeparamref name="TPolicy"/>:
    /// egglog's type-directed pool lookup, <c>PoolSet::get_pool::&lt;T&gt;()</c> and <c>PoolSet::get::&lt;T&gt;()</c>
    /// through <c>InPoolSet</c> (core-relations/src/pool/mod.rs:366-414). Code that pools a type uses this rather
    /// than a pool of its own, so that an object rented in one module and returned in another goes back to the one
    /// pool for its type, and the per-type byte limit is a per-type limit, as in egglog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is exactly one pool per (<typeparamref name="T"/>, <typeparamref name="TPolicy"/>) pair, created on first
    /// use with the limit <typeparamref name="TPolicy"/> states (<see cref="IPoolPolicy{T}.LimitBytes"/>) and
    /// registered with <see cref="PoolSet.Shared"/>, so that <see cref="PoolSet.Clear"/> on it releases this pool too.
    /// Looking the pool up costs nothing: the JIT treats the initialized static readonly field as a constant, which
    /// is what egglog's static <c>InPoolSet</c> dispatch achieves. Two egglog pools whose C# types coincide (such as
    /// <c>Vec&lt;Value&gt;</c> and <c>Vec&lt;u32&gt;</c>, both <c>List&lt;uint&gt;</c>) stay apart by using different
    /// policy types.
    /// </para>
    /// <para>
    /// Thread-safe, with per-thread stacks and limits; see <see cref="ObjectPool{T, TPolicy}"/>. Ownership is explicit:
    /// return a rented object once, or let the garbage collector have it.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The pooled type.</typeparam>
    /// <typeparam name="TPolicy">How to create, clear, admit and weigh <typeparamref name="T"/>, and the pool's limit.</typeparam>
    internal static class SharedPool<T, TPolicy>
        where T : class
        where TPolicy : struct, IPoolPolicy<T>
    {

        /// <summary>
        /// Gets the pool. egglog's <c>get_pool</c>.
        /// </summary>
        public static readonly ObjectPool<T, TPolicy> Instance = PoolSet.Shared.CreatePool<T, TPolicy>(TPolicy.LimitBytes);

        /// <summary>
        /// Gets an empty object from the calling thread's stack, or a new one. egglog's <c>PoolSet::get</c>. O(1).
        /// </summary>
        /// <returns>An empty object the caller now owns.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Rent()
        {
            return Instance.Rent();
        }

        /// <summary>
        /// Hands <paramref name="item"/> back to the pool; see <see cref="ObjectPool{T, TPolicy}.Return"/>. egglog's
        /// <c>Drop for Pooled&lt;T&gt;</c>.
        /// </summary>
        /// <param name="item">An object the caller owns and will not touch again.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Return(T item)
        {
            Instance.Return(item);
        }

        /// <summary>
        /// Clears <paramref name="item"/> and swaps in a reusable pooled object if it is too small to keep; see
        /// <see cref="ObjectPool{T, TPolicy}.Refresh"/>. egglog's <c>Pooled::refresh</c>.
        /// </summary>
        /// <param name="item">The object to refresh; may be replaced.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Refresh(ref T item)
        {
            Instance.Refresh(ref item);
        }

    }

}
