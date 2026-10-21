using System;

namespace Eggnog
{

    /// <summary>
    /// The canonical id of a class a primitive's result holds, for the engine call running on this thread: a rule's or a
    /// merge's through its execution state, a host write's through its batch. A container of classes a primitive
    /// returns then holds canonical classes, as a class it returns is canonical, even when the primitive kept one the
    /// host held, stale since.
    /// </summary>
    internal static class ClassCanonicalizer
    {

        [ThreadStatic]
        static Func<uint, uint>? current;

        /// <summary>
        /// Sets the canonicalizer of the calls on this thread until <see cref="Exit"/>.
        /// </summary>
        /// <param name="find">The canonical id of a class cell.</param>
        /// <returns>The canonicalizer it replaces, for <see cref="Exit"/>.</returns>
        public static Func<uint, uint>? Enter(Func<uint, uint> find)
        {
            var previous = current;
            current = find;
            return previous;
        }

        /// <summary>
        /// Restores the canonicalizer <see cref="Enter"/> replaced.
        /// </summary>
        /// <param name="previous">What <see cref="Enter"/> returned.</param>
        public static void Exit(Func<uint, uint>? previous) => current = previous;

        /// <summary>
        /// Gets a class cell's canonical id through the thread's canonicalizer, or the cell when none is set.
        /// </summary>
        /// <param name="cell">The class cell.</param>
        /// <returns>The canonical cell.</returns>
        public static uint Canonical(uint cell) => current is { } find ? find(cell) : cell;

    }

}
