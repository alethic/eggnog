using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A read-only reference to a value allocated in a <see cref="SharedArena"/>. Copyable, and readable from any
    /// thread. The Rust's <c>SharedRef</c>.
    /// </summary>
    /// <remarks>
    /// Holds the stored object: the value itself for a reference type, the arena's box for a value type. Reading a
    /// reference-type value through <see cref="Value"/> is a field load. <see cref="Value"/> copies a value-type value
    /// out of its box; <see cref="SharedRefExtensions.ValueRef{T}(SharedRef{T})"/> instead returns a read-only
    /// reference to the arena's copy, with no copy, as the Rust's <c>Deref</c> to <c>&amp;T</c> does
    /// (shared_arena.rs:223-232).
    /// </remarks>
    /// <typeparam name="T">The value type.</typeparam>
    internal readonly struct SharedRef<T>
    {

        readonly object? storage;

        /// <summary>
        /// Initializes a new instance over the arena's copy of a value.
        /// </summary>
        /// <param name="storage">The value as stored: itself for a reference type, boxed for a value type.</param>
        internal SharedRef(object? storage)
        {
            this.storage = storage;
        }

        /// <summary>
        /// Gets the stored object: the value for a reference type, the arena's box for a value type.
        /// </summary>
        internal object? Storage
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => storage;
        }

        /// <summary>
        /// Gets the referenced value. For a value type this is a copy; use
        /// <see cref="SharedRefExtensions.ValueRef{T}(SharedRef{T})"/> to read a large struct in place.
        /// </summary>
        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // The branch folds away per instantiation. A value type is unboxed (a type check and a copy); a
                // reference is reinterpreted without a cast, since only a T was ever stored.
                if (typeof(T).IsValueType)
                    return (T)storage!;

                var reference = storage;
                return Unsafe.As<object?, T>(ref reference);
            }
        }

        /// <inheritdoc />
        public override string? ToString() => storage?.ToString();

    }

}
