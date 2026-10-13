using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Per-thread scratch arrays for gathering operand tuples (<see cref="ArgGather"/>): the role egglog's thread-local
    /// pools play for the vectors its dynamic argument path allocates per lane (core-relations/src/action/mask.rs:285-299).
    /// </summary>
    /// <remarks>
    /// Renting takes the thread's array out (or allocates one) and returning puts it back, so a rent made while an outer
    /// rent is live (an external function calling back into the engine) gets an array of its own, and an exception that
    /// skips the return only costs a reallocation later. Per-thread rather than per-<see cref="ExecutionState"/>
    /// because the executor clones a fresh state for every batch.
    /// </remarks>
    internal static class ActionScratch
    {

        [ThreadStatic]
        static uint[]? cells;

        [ThreadStatic]
        static int[]? slots;

        /// <summary>
        /// Rents a cell array of at least <paramref name="minimum"/> cells.
        /// </summary>
        /// <param name="minimum">The number of cells needed.</param>
        /// <returns>The array, which the caller returns with <see cref="Return(uint[])"/>.</returns>
        public static uint[] RentCells(int minimum)
        {
            var array = cells;
            if (array is null || array.Length < minimum)
                return new uint[Math.Max(minimum, 16)];

            cells = null;
            return array;
        }

        /// <summary>
        /// Rents a slot array of at least <paramref name="minimum"/> cells.
        /// </summary>
        /// <param name="minimum">The number of cells needed.</param>
        /// <returns>The array, which the caller returns with <see cref="Return(int[])"/>.</returns>
        public static int[] RentSlots(int minimum)
        {
            var array = slots;
            if (array is null || array.Length < minimum)
                return new int[Math.Max(minimum, 32)];

            slots = null;
            return array;
        }

        /// <summary>
        /// Returns a cell array rented on this thread, keeping the larger of it and the one already held.
        /// </summary>
        /// <param name="array">The array.</param>
        public static void Return(uint[] array)
        {
            if (cells is null || cells.Length < array.Length)
                cells = array;
        }

        /// <summary>
        /// Returns a slot array rented on this thread, keeping the larger of it and the one already held.
        /// </summary>
        /// <param name="array">The array.</param>
        public static void Return(int[] array)
        {
            if (slots is null || slots.Length < array.Length)
                slots = array;
        }

    }

}
