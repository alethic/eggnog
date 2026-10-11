namespace Eggnog.Relations
{

    /// <summary>
    /// The matches of one action waiting to run: egglog's <c>ActionState</c>
    /// (core-relations/src/free_join/execute.rs:638-652), a <see cref="Relations.Bindings"/> batch of up to
    /// <see cref="Bindings.DefaultBatchSize"/> matches and how many it holds.
    /// </summary>
    /// <remarks>
    /// egglog also counts <c>n_runs</c>, which nothing reads; it is left out. Owned by one
    /// <see cref="ActionBuffer"/>, so not thread-safe.
    /// </remarks>
    internal sealed class ActionBatch
    {

        /// <summary>
        /// Gets or sets the batch; replaced when a full batch is handed to another thread.
        /// </summary>
        public Bindings Bindings { get; set; } = new Bindings(Bindings.DefaultBatchSize);

        /// <summary>
        /// Gets or sets the number of matches pushed since the batch last ran.
        /// </summary>
        public int Length { get; set; }

    }

}
