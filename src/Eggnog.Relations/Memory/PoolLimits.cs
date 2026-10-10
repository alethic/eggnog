namespace Eggnog.Relations
{

    /// <summary>
    /// egglog's per-thread byte limits for each pooled type, from the <c>pool_set!</c> declaration in
    /// core-relations/src/pool/mod.rs:424-446. A module that pools the C# counterpart of one of these types returns
    /// the matching limit from its policy's <see cref="IPoolPolicy{T}.LimitBytes"/>, so that
    /// <see cref="SharedPool{T, TPolicy}"/> retains what egglog retains.
    /// </summary>
    internal static class PoolLimits
    {

        /// <summary>
        /// <c>vec_vals: Vec&lt;Value&gt;</c>: scratch value vectors. 32 MiB.
        /// </summary>
        public const long VecValues = 1L << 25;

        /// <summary>
        /// <c>vec_cell_vals: Vec&lt;Cell&lt;Value&gt;&gt;</c>: row buffer storage. 32 MiB. The C# row buffers rent
        /// their arrays from <see cref="System.Buffers.ArrayPool{T}.Shared"/> through <see cref="PooledArray"/>, which
        /// never pools an array over this limit (<see cref="PooledArray.MaxPooledBytes"/>).
        /// </summary>
        public const long VecCellValues = 1L << 25;

        /// <summary>
        /// <c>rows: Vec&lt;RowId&gt;</c>: row id lists such as subset offsets. 32 MiB.
        /// </summary>
        public const long Rows = 1L << 25;

        /// <summary>
        /// <c>offset_vec: SortedOffsetVector</c>. 1 MiB.
        /// </summary>
        public const long OffsetVec = 1L << 20;

        /// <summary>
        /// <c>column_index: IndexMap&lt;Value, BufferedSubset&gt;</c>. 1 MiB.
        /// </summary>
        public const long ColumnIndex = 1L << 20;

        /// <summary>
        /// <c>constraints: Vec&lt;Constraint&gt;</c>. 1 MiB.
        /// </summary>
        public const long Constraints = 1L << 20;

        /// <summary>
        /// <c>bitsets: FixedBitSet</c>. 1 MiB. (egglog's bitset policy never actually keeps one; see
        /// pool/mod.rs:143-153.)
        /// </summary>
        public const long Bitsets = 1L << 20;

        /// <summary>
        /// <c>instrs: Vec&lt;Instr&gt;</c>. 1 MiB.
        /// </summary>
        public const long Instrs = 1L << 20;

        /// <summary>
        /// <c>tuple_indexes: HashTable&lt;TableEntry&lt;BufferedSubset&gt;&gt;</c>. 1 MiB.
        /// </summary>
        public const long TupleIndexes = 1L << 20;

        /// <summary>
        /// <c>staged_outputs: HashTable&lt;table::TableEntry&gt;</c>. 32 MiB.
        /// </summary>
        public const long StagedOutputs = 1L << 25;

        /// <summary>
        /// <c>predicted_vals: PredictedVals</c>. 1 MiB.
        /// </summary>
        public const long PredictedVals = 1L << 20;

        /// <summary>
        /// <c>shard_hist: DenseIdMap&lt;ShardId, usize&gt;</c>. 1 MiB.
        /// </summary>
        public const long ShardHist = 1L << 20;

        /// <summary>
        /// <c>instr_indexes: Vec&lt;u32&gt;</c>. 1 MiB.
        /// </summary>
        public const long InstrIndexes = 1L << 20;

        /// <summary>
        /// <c>cached_subsets: IdVec&lt;ColumnId, OnceLock&lt;Arc&lt;ColumnIndex&gt;&gt;&gt;</c>. 4 MiB.
        /// </summary>
        public const long CachedSubsets = 4L << 20;

        /// <summary>
        /// <c>intersected_on: DenseIdMap&lt;AtomId, i64&gt;</c>. 1 MiB.
        /// </summary>
        public const long IntersectedOn = 1L << 20;

        /// <summary>
        /// <c>cached_child: IdVec&lt;ColumnId, RwLock&lt;HashMap&lt;Value, Arc&lt;TrieNode&gt;&gt;&gt;&gt;</c>. 1 MiB.
        /// </summary>
        public const long CachedChild = 1L << 20;

    }

}
