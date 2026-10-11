using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A structured value that the engine hash-conses to an id, like a base value, but whose contents may mention
    /// e-class ids and so must be rebuilt against the union-find: egglog's <c>ContainerValue</c> trait
    /// (core-relations/src/containers/mod.rs:259-278). The frontend's vectors, sets, maps, multisets and partial
    /// functions implement it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations are structs, so that <see cref="ContainerEnv{TContainer}"/> is specialized for each container
    /// type and every hash, comparison, rebuild and element visit is a direct, inlinable call, the way egglog's
    /// environment is monomorphized over <c>C</c>.
    /// </para>
    /// <para>
    /// A container is immutable once handed to the engine: neither the caller nor the container may change its contents
    /// afterwards (a struct wrapping an array must not expose or reuse that array). Where egglog rebuilds a container in
    /// place (<c>rebuild_contents(&amp;mut self)</c>), <see cref="TryRebuild"/> returns a rebuilt copy and leaves the
    /// original untouched. That keeps the old contents available to remove from the element index, which is what lets
    /// the environment keep that index exact (map §8.1 row 13), and it makes a container read back from the environment
    /// safe to keep without the lock guard egglog's <c>get_val</c> returns.
    /// </para>
    /// <para>
    /// Equality (<see cref="IEquatable{T}.Equals(T)"/>) and <see cref="ComputeHash"/> must agree: equal containers hash
    /// equally. Both are structural over the contents.
    /// </para>
    /// </remarks>
    /// <typeparam name="TSelf">The implementing struct.</typeparam>
    internal interface IContainerValue<TSelf> : IEquatable<TSelf>
        where TSelf : struct, IContainerValue<TSelf>
    {

        /// <summary>
        /// Computes the 64-bit hash of the contents, which picks the container's shard and slot in the hash-cons:
        /// egglog's <c>hash_container</c> (mod.rs:300-304), an <see cref="FxHasher"/> over the derived <c>Hash</c>.
        /// Must be deterministic and consistent with equality. O(size).
        /// </summary>
        /// <returns>The hash.</returns>
        ulong ComputeHash();

        /// <summary>
        /// Rebuilds the contents against <paramref name="rebuilder"/> (the union-find's canonicalizer): egglog's
        /// <c>rebuild_contents</c>. Leaves this container unchanged.
        /// </summary>
        /// <remarks>
        /// Return <c>false</c> exactly when rebuilding changes nothing, that is, when the rebuilt container would compare
        /// equal to this one; the environment then keeps this instance. A container over a base sort (egglog's
        /// <c>do_rebuild = false</c>) always returns <c>false</c>. Make one virtual call into the rebuilder per container
        /// (<see cref="Rebuilder.RebuildSlice"/> over a scratch copy, say) rather than one per element, and allocate the
        /// rebuilt copy only when something changed.
        /// </remarks>
        /// <param name="rebuilder">The rebuilder.</param>
        /// <param name="rebuilt">The rebuilt container when the result is <c>true</c>; otherwise <c>default</c>.</param>
        /// <returns><c>true</c> if the contents changed.</returns>
        bool TryRebuild(Rebuilder rebuilder, out TSelf rebuilt);

        /// <summary>
        /// Visits every value the container holds: egglog's <c>iter</c>. The environment indexes containers by these
        /// values to find the ones a union affects, so every value that rebuilding could change must be visited (a value
        /// that is not may leave the container stale after an incremental rebuild). Values may repeat. Must visit the
        /// same sequence every time for the same container.
        /// </summary>
        /// <typeparam name="TVisitor">The visitor type; a struct, so the loop is specialized for it.</typeparam>
        /// <param name="visitor">Receives each value.</param>
        void ForEachValue<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IContainerElementVisitor;

    }

}
