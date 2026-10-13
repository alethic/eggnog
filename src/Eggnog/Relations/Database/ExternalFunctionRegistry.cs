using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The database's external functions, by <see cref="ExternalFunctionId"/>, with freed ids reused: egglog's
    /// <c>ExternalFunctions</c>, a <c>DenseIdMapWithReuse</c> (core-relations/src/free_join/mod.rs:258-259,
    /// numeric-id/src/lib.rs:306-356).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Add"/> takes the most recently freed id if there is one, otherwise the next unused id.
    /// </para>
    /// <para>
    /// Threading: the registry is shared by every context of a store, whose children may run concurrently while the
    /// root registers or frees a function. So the map is copy-on-write: a published snapshot is never changed;
    /// <see cref="Add"/> and <see cref="Remove"/> take a lock, clone the snapshot, change the clone and publish it with
    /// a volatile write. Lookups read the current snapshot with one volatile load and take no lock, O(1). Adding and
    /// removing are O(functions), which is fine for definitions.
    /// </para>
    /// </remarks>
    internal sealed class ExternalFunctionRegistry
    {

        readonly object sync = new();
        readonly List<ExternalFunctionId> free;

        /// <summary>
        /// The published snapshot; replaced under <see cref="sync"/>, never changed in place.
        /// </summary>
        DenseIdMap<ExternalFunctionId, ExternalFunction> functions;

        /// <summary>
        /// Initializes an empty registry.
        /// </summary>
        public ExternalFunctionRegistry()
        {
            functions = new DenseIdMap<ExternalFunctionId, ExternalFunction>();
            free = new List<ExternalFunctionId>();
        }

        /// <summary>
        /// Initializes a copy, for <see cref="Clone"/>.
        /// </summary>
        ExternalFunctionRegistry(DenseIdMap<ExternalFunctionId, ExternalFunction> functions, List<ExternalFunctionId> free)
        {
            this.functions = functions;
            this.free = free;
        }

        /// <summary>
        /// Gets the number of registered functions. Linear in the largest id; lock-free.
        /// </summary>
        public int Count => Volatile.Read(ref functions).Count;

        /// <summary>
        /// Gets the function with id <paramref name="id"/>. Lock-free, O(1).
        /// </summary>
        /// <param name="id">The id.</param>
        /// <exception cref="KeyNotFoundException">No such function, or it was freed.</exception>
        public ExternalFunction this[ExternalFunctionId id] => Volatile.Read(ref functions)[id];

        /// <summary>
        /// Registers <paramref name="function"/>. egglog's <c>push</c>: reuses the most recently freed id, if any.
        /// Thread-safe against lookups and other registrations.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>Its id.</returns>
        public ExternalFunctionId Add(ExternalFunction function)
        {
            ArgumentNullException.ThrowIfNull(function);

            lock (sync)
            {
                var next = functions.Clone();
                ExternalFunctionId id;
                if (free.Count > 0)
                {
                    id = free[^1];
                    free.RemoveAt(free.Count - 1);
                    next.Insert(id, function);
                }
                else
                {
                    id = next.Push(function);
                }

                Volatile.Write(ref functions, next);
                return id;
            }
        }

        /// <summary>
        /// Unregisters the function with id <paramref name="id"/>, freeing the id for reuse. egglog's <c>take</c>; an
        /// absent id is ignored. Thread-safe against lookups and registrations.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="function">The function that was registered.</param>
        /// <returns><c>true</c> if a function was removed.</returns>
        public bool Remove(ExternalFunctionId id, [MaybeNullWhen(false)] out ExternalFunction function)
        {
            lock (sync)
            {
                if (functions.TryGetValue(id, out function) == false)
                    return false;

                var next = functions.Clone();
                next.TryTake(id, out _);
                free.Add(id);
                Volatile.Write(ref functions, next);
                return true;
            }
        }

        /// <summary>
        /// Gets the function with id <paramref name="id"/>, if registered. Lock-free, O(1).
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="function">The function.</param>
        /// <returns><c>true</c> if registered.</returns>
        public bool TryGet(ExternalFunctionId id, [MaybeNullWhen(false)] out ExternalFunction function) => Volatile.Read(ref functions).TryGetValue(id, out function);

        /// <summary>
        /// Creates a registry with the same ids and free list, sharing the function instances (see
        /// <see cref="ExternalFunction"/>).
        /// </summary>
        /// <returns>The copy.</returns>
        public ExternalFunctionRegistry Clone()
        {
            lock (sync)
                return new ExternalFunctionRegistry(functions.Clone(), new List<ExternalFunctionId>(free));
        }

    }

}
