namespace Eggnog.Relations
{

    /// <summary>
    /// Counts of what a child context's layering did (colored-contexts spec §6, §14.2): the per-child cost measures that
    /// tests and benchmarks read to check that a child pays in proportion to what it touches.
    /// </summary>
    /// <remarks>
    /// Plain fields, incremented without synchronization on the paths that count them: those run on the context's domain
    /// thread, except <see cref="LayersCreated"/>, which is counted under the layer lock. Under concurrent readers a count
    /// may lose increments; it is a measure, never a decision.
    /// </remarks>
    internal sealed class LayerStatistics
    {

        /// <summary>
        /// The layers the context created: one per table it first wrote, hid or cleared (§6.2).
        /// </summary>
        public long LayersCreated;

        /// <summary>
        /// The ancestor rows the context hid, all reasons together (§6.3).
        /// </summary>
        public long RowsHidden;

        /// <summary>
        /// The key-index probes of write-phase lookups (§6.5): one per layer probed.
        /// </summary>
        public long LookupProbes;

        /// <summary>
        /// The hidden-set unions built for views, when two or more levels that are not frozen hide rows of one layer
        /// (§6.3).
        /// </summary>
        public long FilterMerges;

        /// <summary>
        /// The incoming rows whose id cells the merge rewrote to their canonical ids (X38).
        /// </summary>
        public long RowsCanonicalized;

    }

}
