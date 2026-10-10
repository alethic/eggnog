using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Accessors for <see cref="SharedRef{T}"/> that need a constraint the struct itself cannot carry.
    /// </summary>
    internal static class SharedRefExtensions
    {

        /// <summary>
        /// Gets a read-only reference to the arena's copy of a value-type value, without copying it: the Rust's
        /// <c>Deref</c> of <c>SharedRef</c> to <c>&amp;T</c> (shared_arena.rs:223-232). The reference points into the
        /// arena's box on the heap, so it stays valid as long as it is used, independently of
        /// <paramref name="reference"/>.
        /// </summary>
        /// <remarks>
        /// Only value types need this: <see cref="SharedRef{T}.Value"/> of a reference type already returns the
        /// arena's object itself. Cost: an unbox, which is one type check.
        /// </remarks>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="reference">The shared reference; must come from <see cref="SharedArenaHandle.Alloc{T}"/>.</param>
        /// <returns>A read-only reference to the arena's copy.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly T ValueRef<T>(this SharedRef<T> reference)
            where T : struct
        {
            return ref Unsafe.Unbox<T>(reference.Storage!);
        }

    }

}
