using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// Hands out process-wide slot numbers to container types, one per type on first use, so that a
    /// <see cref="ContainerValues"/> finds a type's environment with an array index instead of egglog's hash lookup of the
    /// <c>TypeId</c> (core-relations/src/containers/mod.rs:48-67, 129-133).
    /// </summary>
    internal static class ContainerTypeSlots
    {

        static int next = -1;

        /// <summary>
        /// Allocates the next slot number. Thread-safe.
        /// </summary>
        /// <returns>The slot number, from zero up.</returns>
        public static int Allocate() => Interlocked.Increment(ref next);

    }

}
