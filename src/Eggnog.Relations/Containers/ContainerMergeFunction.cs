namespace Eggnog.Relations
{

    /// <summary>
    /// Resolves two ids found for equal containers: egglog's container <c>MergeFn</c>
    /// (core-relations/src/containers/mod.rs:39-46), <c>Fn(&amp;mut ExecutionState, Value, Value) -&gt; Value</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container rebuild calls it when a rebuilt container collides with one already in the hash-cons: the result
    /// becomes the id of the surviving entry. The bridge's merge unions the two ids in the union-find and keeps the
    /// smaller one (<see cref="UnionContainerMerge"/>), which is the id the union-find keeps canonical, so the
    /// surviving id is the future canonical id.
    /// </para>
    /// <para>
    /// Called once per collision through one virtual call, as egglog calls its boxed closure; never per element. It may
    /// stage writes through the state. A parallel rebuild calls it from several threads at once, each with its own
    /// clone of the state, so it must not keep mutable state of its own.
    /// </para>
    /// </remarks>
    internal abstract class ContainerMergeFunction
    {

        /// <summary>
        /// Merges the id <paramref name="incoming"/> of a rebuilt container into the id <paramref name="current"/> of
        /// the equal container already present.
        /// </summary>
        /// <param name="state">The execution state, for staging writes (a union, say).</param>
        /// <param name="current">The id of the container already in the hash-cons.</param>
        /// <param name="incoming">The id of the container being inserted.</param>
        /// <returns>The id the merged container keeps.</returns>
        public abstract uint Merge(ExecutionState state, uint current, uint incoming);

    }

}
