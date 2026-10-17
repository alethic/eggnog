namespace Eggnog.Relations
{

    /// <summary>
    /// Where an <see cref="ExecutionState"/> was cloned (<see cref="ExecutionState.Clone(ExecutionStateCloneSite)"/>):
    /// the key of the executor's clone counts (<see cref="ExecutorCounters.Clones"/>).
    /// </summary>
    internal enum ExecutionStateCloneSite
    {

        /// <summary>
        /// A clone made without naming a site (<see cref="ExecutionState.Clone()"/>).
        /// </summary>
        Other,

        /// <summary>
        /// A full action batch run inline, on the calling thread (<see cref="ActionBuffer.Push"/> without a scope, or
        /// with a backlogged one).
        /// </summary>
        InlineBatch,

        /// <summary>
        /// A full action batch handed to a parallel scope as a task of its own (<see cref="ActionBuffer.Push"/>).
        /// </summary>
        SpawnedBatch,

        /// <summary>
        /// The partial batches a join task leaves at its end, run on one clone (<see cref="ActionBuffer.FlushOnClone"/>):
        /// once per serial rule-set run, once per parallel rule task or morsel that pushed matches.
        /// </summary>
        PartialBatches,

        /// <summary>
        /// One shard's staging task of a parallel insert into a <see cref="SortedWritesTable"/>.
        /// </summary>
        InsertShard,

        /// <summary>
        /// One task of a parallel rebuild of a <see cref="SortedWritesTable"/>, of all its rows or of a subset.
        /// </summary>
        TableRebuild,

        /// <summary>
        /// One container environment's task of a parallel container rebuild (<see cref="ContainerValues"/>).
        /// </summary>
        ContainerRebuild,

    }

}
