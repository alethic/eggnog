namespace Eggnog.Relations
{

    /// <summary>
    /// Counts of what a child context's layering did: the per-child cost measures that tests and benchmarks read to
    /// check that a child pays in proportion to what it touches.
    /// </summary>
    /// <remarks>
    /// Plain fields, incremented without synchronization on the paths that count them: those run on the context's domain
    /// thread, except <see cref="LayersCreated"/>, which is counted under the layer lock. Under concurrent readers a count
    /// may lose increments; it is a measure, never a decision.
    /// </remarks>
    internal sealed class LayerStatistics
    {

        /// <summary>
        /// The layers the context created: one per table it first wrote, hid or cleared.
        /// </summary>
        public long LayersCreated;

        /// <summary>
        /// The ancestor rows the context hid, all reasons together.
        /// </summary>
        public long RowsHidden;

        /// <summary>
        /// The key-index probes of write-phase lookups: one per layer probed.
        /// </summary>
        public long LookupProbes;

        /// <summary>
        /// The hidden-set unions built for views, when two or more levels that are not frozen hide rows of one layer.
        /// </summary>
        public long FilterMerges;

        /// <summary>
        /// The incoming rows whose id cells the merge rewrote to their canonical ids.
        /// </summary>
        public long RowsCanonicalized;

        /// <summary>
        /// The ancestor rows a row catch-up looked at: every row an unsettled ancestor appended since the context last
        /// caught up, live or not. Zero under frozen ancestors once the context has read them.
        /// </summary>
        public long CatchUpRowsExamined;

        /// <summary>
        /// The distinct ancestor rows the hide-and-copy step of a rebuild pass found through the ancestors' rebuild
        /// indexes under the context's new own losers: the uses of those losers, live or not; never a scan.
        /// </summary>
        public long AncestorRowsExamined;

        /// <summary>
        /// The ancestor rows the context copied into its own layers and hid: by catch-up, for each of its branches, and
        /// by the hide-and-copy step of a rebuild pass. The copy volume of hiding by losers only: a union copies every
        /// visible ancestor row that names its loser.
        /// </summary>
        public long RowsCopied;

        /// <summary>
        /// The ancestor rows a catch-up copied only to re-stamp them, because their timestamp lies below the context's
        /// semi-naive floor, a safeguard of the rule watermarks. Zero whenever writers stamp with their state's
        /// timestamp, which tests assert.
        /// </summary>
        public long RestampedCopies;

    }

}
