using System;
using System.Threading;

namespace Eggnog.Relations
{

    internal sealed partial class EGraphContext
    {

        /// <summary>
        /// Serializes the growth of <see cref="locals"/> and the installation of a value.
        /// </summary>
        readonly object localsGate = new();

        /// <summary>
        /// The context-local values, by slot index; published whole when the array grows, each element with a volatile
        /// write.
        /// </summary>
        object?[] locals = Array.Empty<object?>();

        /// <summary>
        /// Gets the context's value for <paramref name="slot"/>, building it with the slot's factory the first time.
        /// Thread-safe: the context's rule tasks may ask too.
        /// </summary>
        /// <remarks>
        /// Lock-free once the value exists (one volatile read). The factory runs outside the lock, so it may read the
        /// context; when two threads race, both may build a value, and the first installed is kept and returned to both.
        /// A frozen context's values are built on demand like any other's.
        /// </remarks>
        /// <typeparam name="T">The value's type.</typeparam>
        /// <param name="slot">A slot of this context's e-graph.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The slot belongs to another e-graph.</exception>
        /// <exception cref="ObjectDisposedException">The context is disposed.</exception>
        /// <exception cref="InvalidOperationException">The factory returned <c>null</c>.</exception>
        public T GetLocal<T>(ContextLocalSlot<T> slot)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(slot);
            if (ReferenceEquals(slot.Graph, owner) == false)
                throw new ArgumentException("The slot belongs to another e-graph.", nameof(slot));

            context.ThrowIfDisposed();
            var index = slot.Index;
            var current = Volatile.Read(ref locals);
            if ((uint)index < (uint)current.Length && Volatile.Read(ref current[index]) is T existing)
                return existing;

            var created = slot.Factory(this) ?? throw new InvalidOperationException("A context-local slot's factory returned null.");
            lock (localsGate)
            {
                current = locals;
                if (index >= current.Length)
                {
                    var grown = new object?[Math.Max(index + 1, Math.Max(owner.LocalSlotCount, current.Length * 2))];
                    Array.Copy(current, grown, current.Length);
                    Volatile.Write(ref locals, grown);
                    current = grown;
                }

                if (current[index] is T winner)
                    return winner;

                Volatile.Write(ref current[index], created);
                return created;
            }
        }

        /// <summary>
        /// Drops the context's local values: it is disposed.
        /// </summary>
        void ReleaseLocals()
        {
            lock (localsGate)
                Volatile.Write(ref locals, Array.Empty<object?>());
        }

    }

}
