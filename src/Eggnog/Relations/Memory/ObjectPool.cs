using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// A pool of reusable objects of one type, with a per-thread byte limit. egglog's <c>Pool&lt;T&gt;</c> together
    /// with the drop-time return of <c>Pooled&lt;T&gt;</c> (core-relations/src/pool/mod.rs:210-364).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each thread has its own stack of cleared objects, as in egglog, where every pool lives in a thread-local
    /// <c>PoolSet</c>: <see cref="Rent"/> pops from the calling thread's stack and <see cref="Return"/> pushes onto
    /// it, so an object rented on one thread and returned on another lands in the second thread's stack. The byte
    /// limit applies per thread, as egglog's per-type limits do. Any thread may call any member at any time.
    /// </para>
    /// <para>
    /// Engine code reaches the one pool for a type through <see cref="SharedPool{T, TPolicy}"/>, egglog's
    /// type-directed lookup; constructing a pool directly gives a separate pool (egglog's <c>Pool::default</c> or
    /// <c>Pool::new</c>), for tests or a structure that wants a private one.
    /// </para>
    /// <para>
    /// Rust returns a <c>Pooled&lt;T&gt;</c> to its pool when it is dropped. C# has no destructors to lean on, so
    /// ownership is explicit: whoever holds a rented object either calls <see cref="Return"/> exactly once when done
    /// with it, or simply lets it go, in which case the garbage collector reclaims it and nothing leaks. Returning an
    /// object twice, or using it after returning it, hands it to two owners; debug builds assert against a double
    /// return on the same thread. There are no finalizers.
    /// </para>
    /// <para>
    /// The policy is a struct type parameter, so the JIT specializes the pool per policy and the policy calls are
    /// direct (the counterpart of Rust's monomorphized <c>Clear</c> impls). Rent and return cost a thread-local
    /// lookup, one uncontended compare-exchange and O(1) stack work, plus whatever the policy's clear costs.
    /// </para>
    /// <para>
    /// The pool tracks each thread's stack only weakly, so the stack of a thread that exits (thread-pool threads
    /// retire when idle) is collected with everything in it. egglog's thread-local pool set is never dropped and
    /// leaks with its thread; rayon's threads just never exit.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The pooled type.</typeparam>
    /// <typeparam name="TPolicy">How to create, clear, admit and weigh <typeparamref name="T"/>.</typeparam>
    internal sealed class ObjectPool<T, TPolicy> : IPool
        where T : class
        where TPolicy : struct, IPoolPolicy<T>
    {

        readonly ThreadLocal<ObjectPoolState<T>> states;
        readonly List<WeakReference<ObjectPoolState<T>>> allStates = new();
        readonly long limitBytes;

        /// <summary>
        /// Initializes a pool with no byte limit, like egglog's <c>Pool::default</c>.
        /// </summary>
        public ObjectPool()
            : this(long.MaxValue)
        {

        }

        /// <summary>
        /// Initializes a pool that holds at most <paramref name="limitBytes"/> bytes of objects per thread, as
        /// estimated by <typeparamref name="TPolicy"/>.
        /// </summary>
        /// <param name="limitBytes">The per-thread byte limit. Zero keeps only objects that weigh nothing.</param>
        public ObjectPool(long limitBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);
            this.limitBytes = limitBytes;
            states = new ThreadLocal<ObjectPoolState<T>>(CreateState);
        }

        /// <summary>
        /// Gets the per-thread byte limit.
        /// </summary>
        public long LimitBytes => limitBytes;

        /// <summary>
        /// Gets the number of objects the calling thread's stack holds. Diagnostics.
        /// </summary>
        public int CountOnCurrentThread => states.Value!.Count;

        /// <summary>
        /// Gets the bytes charged to the calling thread's stack. Diagnostics.
        /// </summary>
        public long BytesOnCurrentThread => states.Value!.Bytes;

        /// <summary>
        /// Gets an empty object: the one most recently returned on this thread, or a new one from
        /// <typeparamref name="TPolicy"/> when the thread's stack is empty. egglog's <c>Pool::get</c>. O(1).
        /// </summary>
        /// <returns>An empty object the caller now owns.</returns>
        public T Rent()
        {
            var state = states.Value!;
            state.Enter();
            var item = state.Pop();
            state.Exit();
            return item ?? TPolicy.Create();
        }

        /// <summary>
        /// Hands <paramref name="item"/> back to the calling thread's stack, or drops it when it is not worth
        /// keeping. egglog's <c>Drop for Pooled&lt;T&gt;</c> followed by <c>PoolState::push</c>: an object the policy
        /// declines is dropped; otherwise it is cleared, checked again (clearing can change the answer), and stored
        /// unless storing it would push the thread past the byte limit. The caller must not touch the object again.
        /// </summary>
        /// <param name="item">An object the caller owns.</param>
        public void Return(T item)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (TPolicy.ShouldReuse(item) == false)
                return;

            TPolicy.Clear(item);
            if (TPolicy.ShouldReuse(item) == false)
                return;

            var bytes = TPolicy.Bytes(item);
            var state = states.Value!;
            state.Enter();
            AssertNotPooled(state, item);
            state.Push(item, bytes, limitBytes);
            state.Exit();
        }

        /// <summary>
        /// Clears <paramref name="item"/> in place, and if it is too small to be worth keeping, swaps in a reusable
        /// object from this thread's stack when there is one (the small one is dropped). egglog's
        /// <c>Pooled::refresh</c>: a buffer that was cleared and is about to be refilled picks up a recycled large
        /// allocation instead of growing a small one again. O(1) plus the clear.
        /// </summary>
        /// <param name="item">The object to refresh; may be replaced.</param>
        public void Refresh(ref T item)
        {
            TPolicy.Clear(item);
            if (TPolicy.ShouldReuse(item))
                return;

            var state = states.Value!;
            state.Enter();
            var other = state.Pop();
            state.Exit();

            // egglog pops a default object from an empty pool and returns because it is not reusable; skipping the
            // allocation is equivalent.
            if (other is null || TPolicy.ShouldReuse(other) == false)
                return;

            item = other;
        }

        /// <summary>
        /// Drops every pooled object on every thread and releases the stacks' storage. egglog's <c>Pool::clear</c>, as
        /// <c>Database::drop</c> runs it on every worker thread. Safe to call while other threads rent and return:
        /// they simply start over with empty stacks. Linear in the number of threads that have used the pool.
        /// </summary>
        public void Clear()
        {
            var live = new List<ObjectPoolState<T>>();
            lock (allStates)
            {
                allStates.RemoveAll(static w => w.TryGetTarget(out _) == false);
                foreach (var weak in allStates)
                    if (weak.TryGetTarget(out var state))
                        live.Add(state);
            }

            foreach (var state in live)
            {
                state.Enter();
                state.ClearAndShrink();
                state.Exit();
            }
        }

        /// <summary>
        /// Creates the calling thread's stack the first time it touches the pool, and remembers it weakly for
        /// <see cref="Clear"/>. Once per thread; prunes the stacks of threads that have exited.
        /// </summary>
        ObjectPoolState<T> CreateState()
        {
            var state = new ObjectPoolState<T>();
            lock (allStates)
            {
                allStates.RemoveAll(static w => w.TryGetTarget(out _) == false);
                allStates.Add(new WeakReference<ObjectPoolState<T>>(state));
            }

            return state;
        }

        [Conditional("DEBUG")]
        static void AssertNotPooled(ObjectPoolState<T> state, T item)
        {
            Debug.Assert(state.Contains(item) == false, "An object was returned to its pool twice.");
        }

    }

}
