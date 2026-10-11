namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The shape of a union-find workload: the bench groups <c>random</c> and <c>local</c> of egglog's
    /// <c>union-find/benches/union-find.rs</c> (and the two benches of its <c>rayon_work_stealing</c> group). Public
    /// because benchmark parameters are.
    /// </summary>
    public enum UnionFindWorkloadKind
    {

        /// <summary>
        /// egglog's <c>prepare_operations_random</c>: unions of the first quarter of the identifiers with random
        /// partners, 90% of them applied before the measurement, then two finds of random identifiers per identifier,
        /// all shuffled.
        /// </summary>
        Random,

        /// <summary>
        /// egglog's <c>prepare_operations_local</c>: unions of 15% of the identifiers with a partner at most 999 above,
        /// 90% of them applied before the measurement, then two finds per identifier, all sorted by first identifier.
        /// </summary>
        Local,

    }

}
