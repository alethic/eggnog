using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog.Relations
{

    /// <summary>
    /// The database's external functions, by <see cref="ExternalFunctionId"/>, with freed ids reused: egglog's
    /// <c>ExternalFunctions</c>, a <c>DenseIdMapWithReuse</c> (core-relations/src/free_join/mod.rs:258-259,
    /// numeric-id/src/lib.rs:306-356).
    /// </summary>
    /// <remarks>
    /// <see cref="Add"/> takes the most recently freed id if there is one, otherwise the next unused id. Lookups are
    /// O(1) and safe from any number of threads while nobody adds or removes; adding and removing need exclusive
    /// access.
    /// </remarks>
    internal sealed class ExternalFunctionRegistry
    {

        readonly DenseIdMap<ExternalFunctionId, ExternalFunction> functions;
        readonly List<ExternalFunctionId> free;

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
        ExternalFunctionRegistry(ExternalFunctionRegistry source)
        {
            functions = source.functions.Clone();
            free = new List<ExternalFunctionId>(source.free);
        }

        /// <summary>
        /// Gets the number of registered functions.
        /// </summary>
        public int Count => functions.Count;

        /// <summary>
        /// Gets the function with id <paramref name="id"/>.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <exception cref="KeyNotFoundException">No such function, or it was freed.</exception>
        public ExternalFunction this[ExternalFunctionId id] => functions[id];

        /// <summary>
        /// Registers <paramref name="function"/>. egglog's <c>push</c>: reuses the most recently freed id, if any.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>Its id.</returns>
        public ExternalFunctionId Add(ExternalFunction function)
        {
            ArgumentNullException.ThrowIfNull(function);

            if (free.Count > 0)
            {
                var id = free[^1];
                free.RemoveAt(free.Count - 1);
                functions.Insert(id, function);
                return id;
            }

            return functions.Push(function);
        }

        /// <summary>
        /// Unregisters the function with id <paramref name="id"/>, freeing the id for reuse. egglog's <c>take</c>; an
        /// absent id is ignored.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="function">The function that was registered.</param>
        /// <returns><c>true</c> if a function was removed.</returns>
        public bool Remove(ExternalFunctionId id, [MaybeNullWhen(false)] out ExternalFunction function)
        {
            if (functions.TryTake(id, out function) == false)
                return false;

            free.Add(id);
            return true;
        }

        /// <summary>
        /// Gets the function with id <paramref name="id"/>, if registered.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="function">The function.</param>
        /// <returns><c>true</c> if registered.</returns>
        public bool TryGet(ExternalFunctionId id, [MaybeNullWhen(false)] out ExternalFunction function) => functions.TryGetValue(id, out function);

        /// <summary>
        /// Creates a registry with the same ids and free list, sharing the function instances (see
        /// <see cref="ExternalFunction"/>).
        /// </summary>
        /// <returns>The copy.</returns>
        public ExternalFunctionRegistry Clone() => new(this);

    }

}
