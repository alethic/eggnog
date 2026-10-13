using System;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// The materialization of a block of a decomposed plan while parallel tasks fill it: one
    /// <see cref="Materialization"/> per thread, merged once the block's scope has finished. It stands in for egglog's
    /// <c>DashMap&lt;Vec&lt;Value&gt;, RowBuffer&gt;</c>, which is converted to an <c>IndexMap</c> after the block
    /// (core-relations/src/free_join/execute.rs:413-470).
    /// </summary>
    /// <remarks>
    /// Each thread adds to its own part without locking. The merged materialization's group and row order depends on
    /// scheduling, as the order egglog's conversion from a <c>DashMap</c> does; its contents do not.
    /// </remarks>
    internal sealed class ConcurrentMaterialization : IDisposable
    {

        readonly int keyArity;
        readonly int valueArity;
        readonly ThreadLocal<Materialization> parts;

        /// <summary>
        /// Initializes an empty materialization.
        /// </summary>
        /// <param name="keyArity">The number of message variables.</param>
        /// <param name="valueArity">The number of cells per row (at least one).</param>
        public ConcurrentMaterialization(int keyArity, int valueArity)
        {
            this.keyArity = keyArity;
            this.valueArity = valueArity;
            parts = new ThreadLocal<Materialization>(CreatePart, trackAllValues: true);
        }

        /// <summary>
        /// Gets the calling thread's part, creating it on first use.
        /// </summary>
        public Materialization Local => parts.Value!;

        /// <summary>
        /// Merges every thread's part into one materialization. Call once, after every task that adds has finished.
        /// </summary>
        /// <returns>The merged materialization, owned by the caller.</returns>
        public Materialization Merge()
        {
            Materialization? result = null;
            foreach (var part in parts.Values)
            {
                if (result is null)
                {
                    result = part;
                    continue;
                }

                result.AddAll(part);
                part.Dispose();
            }

            return result ?? new Materialization(keyArity, valueArity);
        }

        /// <summary>
        /// Releases the per-thread slots. The merged materialization stays valid.
        /// </summary>
        public void Dispose() => parts.Dispose();

        Materialization CreatePart() => new(keyArity, valueArity);

    }

}
