namespace Eggnog.Relations
{

    /// <summary>
    /// Where the join sends complete matches, and how it batches and hands off work: egglog's <c>ActionBuffer</c> trait
    /// (core-relations/src/free_join/execute.rs:1809-1880). Implemented by action sinks, which batch matches for the
    /// rule's action program, and materializer sinks, which fill a decomposed plan's materializations.
    /// </summary>
    /// <remarks>
    /// Implemented by structs and passed by reference as a type argument, so the whole join (stage kernels, drains,
    /// recursion) is specialized per sink and the calls below are direct, the way Rust monomorphizes <c>run_plan</c>
    /// over the buffer type.
    /// </remarks>
    internal interface IJoinSink
    {

        /// <summary>
        /// Gets whether drains at join positions 0 and 1 hand their frames to another task (<see cref="TryRecur"/>).
        /// egglog's <c>supports_parallel_drain</c>.
        /// </summary>
        bool SupportsParallelDrain { get; }

        /// <summary>
        /// Gets the number of frames a stage buffers before draining. egglog's <c>morsel_size</c>.
        /// </summary>
        /// <param name="level">The join position.</param>
        /// <param name="total">The number of stages.</param>
        /// <returns>The morsel size.</returns>
        int MorselSize(int level, int total);

        /// <summary>
        /// Takes the current bindings of <paramref name="run"/> as one match of <paramref name="target"/> (an action
        /// or a materialization index). egglog's <c>push_bindings</c>.
        /// </summary>
        /// <param name="run">The join, whose values are read.</param>
        /// <param name="target">The action or materialization index.</param>
        void PushBindings(JoinRun run, int target);

        /// <summary>
        /// Hands the frames <paramref name="updates"/>, drained at position <paramref name="cur"/>, to another task
        /// together with a copy of <paramref name="run"/>'s state, unless the sink's scope is backlogged. egglog's
        /// <c>recur</c>. Only called when <see cref="SupportsParallelDrain"/> is <c>true</c>.
        /// </summary>
        /// <param name="run">The join.</param>
        /// <param name="cur">The position the frames were produced at.</param>
        /// <param name="updates">The frames; owned by the task from now on if handed off.</param>
        /// <returns><c>true</c> if the frames were handed off; <c>false</c> if the caller must replay them itself.</returns>
        bool TryRecur(JoinRun run, int cur, FrameUpdates updates);

        /// <summary>
        /// Called by a task created by <see cref="TryRecur"/> after it replayed its frames: runs what the task's own
        /// buffer still holds.
        /// </summary>
        void FinishTask();

    }

}
