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

        /// <summary>
        /// The ancestor rows a row catch-up looked at (§7.2): every row an unsettled ancestor appended since the context
        /// last caught up, live or not. Zero under frozen ancestors once the context has read them.
        /// </summary>
        public long CatchUpRowsExamined;

        /// <summary>
        /// The distinct ancestor rows step 4 of a rebuild pass found through the ancestors' rebuild indexes under the
        /// context's new own losers (§7.3): the uses of those losers, live or not; never a scan.
        /// </summary>
        public long AncestorRowsExamined;

        /// <summary>
        /// The ancestor rows the context copied into its own layers and hid (§7.2, §7.3): by catch-up, for each of its
        /// branches, and by step 4. The copy volume of decision 3 (G3).
        /// </summary>
        public long RowsCopied;

        /// <summary>
        /// The ancestor rows a catch-up copied only to re-stamp them, because their timestamp lies below the context's
        /// semi-naive floor (§7.2 branch c′, the C7 safeguard). Zero whenever writers stamp with their state's timestamp
        /// (I21), which tests assert.
        /// </summary>
        public long RestampedCopies;

    }

}
