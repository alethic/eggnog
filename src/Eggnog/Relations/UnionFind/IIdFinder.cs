namespace Eggnog.Relations
{

    /// <summary>
    /// Maps an id to its canonical id in some view, without writing: what a canonicalizing rebuilder applies to every
    /// id cell. The root's union-find (<see cref="RootIdFinder"/>) and a context's layered union-find
    /// (<see cref="LayeredIdFinder"/>) implement it.
    /// </summary>
    /// <remarks>
    /// Implement it on a readonly struct and pass the struct as a type parameter constrained to <c>struct</c>
    /// (<see cref="CanonicalizeLoops"/>), so every per-row loop is specialized for it and the find inlined, the way
    /// Rust monomorphizes a closure: the root's loops then compile to the same machine code as loops that call the
    /// root's union-find directly.
    /// </remarks>
    internal interface IIdFinder
    {

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/>. Never writes; safe from many threads while the union-find
        /// does not change.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The canonical id: the minimum of its class.</returns>
        uint Find(uint id);

    }

}
