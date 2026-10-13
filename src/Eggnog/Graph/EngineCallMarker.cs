using System;

namespace Eggnog
{

    /// <summary>
    /// Marks the current thread as running a user delegate the engine invoked: a primitive, a filter, a lattice merge or
    /// a cost function. The call sites that invoke such delegates enter the marker around each invocation, on
    /// whatever thread runs it, pool threads included, and every graph and scope entry point checks it first, so a
    /// delegate that calls into a graph is refused instead of interleaving with the engine call that invoked it.
    /// </summary>
    /// <remarks>
    /// A [ThreadStatic] depth: one increment and one decrement per invocation, no allocation, no lock.
    /// </remarks>
    internal static class EngineCallMarker
    {

        /// <summary>
        /// The message of the exception a marked thread gets from a graph or scope entry point.
        /// </summary>
        internal const string Message = "Not allowed from a primitive, lattice, filter or cost function";

        [ThreadStatic]
        static int depth;

        /// <summary>
        /// Gets whether the current thread is running a user delegate the engine invoked.
        /// </summary>
        public static bool IsActive => depth > 0;

        /// <summary>
        /// Marks the current thread; pair with <see cref="Exit"/> in a <c>finally</c>.
        /// </summary>
        public static void Enter() => depth++;

        /// <summary>
        /// Unmarks the current thread once, undoing one <see cref="Enter"/>.
        /// </summary>
        public static void Exit() => depth--;

        /// <summary>
        /// Throws when the current thread is marked: the first check of every graph and scope entry point.
        /// </summary>
        /// <exception cref="InvalidOperationException">The current thread runs a user delegate the engine
        /// invoked.</exception>
        public static void ThrowIfActive()
        {
            if (depth > 0)
                throw new InvalidOperationException(Message);
        }

    }

}
