using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The action sink of the parallel executor: egglog's <c>ScopedActionBuffer</c>
    /// (core-relations/src/free_join/execute.rs:1943-2049). Full batches run as tasks of the scope, drains at positions 0
    /// and 1 hand their frames to new tasks, each with its own sink, and the morsel size at position 0 drops to 32 when
    /// there are more than two stages, for more parallel slack.
    /// </summary>
    /// <remarks>
    /// Unlike egglog, a backlogged scope (<see cref="WorkScope.IsBacklogged"/>) makes the sink run batches and replay
    /// morsels inline rather than queue more: the same work, with bounded memory and a prompt early stop.
    /// </remarks>
    internal readonly struct ScopedActionSink : IJoinSink
    {

        readonly ActionBuffer buffer;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="buffer">The task's batches; a buffer with a scope.</param>
        public ScopedActionSink(ActionBuffer buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (buffer.Scope is null)
                throw new ArgumentException("A scoped sink needs a scope.", nameof(buffer));

            this.buffer = buffer;
        }

        /// <summary>
        /// Gets the task's batches.
        /// </summary>
        public ActionBuffer Buffer => buffer;

        /// <inheritdoc />
        public bool SupportsParallelDrain => true;

        /// <inheritdoc />
        public int MorselSize(int level, int total) => level == 0 && total > 2 ? 32 : JoinRun.DefaultMorselSize;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PushBindings(JoinRun run, int target) => buffer.Push(target, run.Values, run.BoundVariables);

        /// <inheritdoc />
        public bool TryRecur(JoinRun run, int cur, FrameUpdates updates)
        {
            var scope = buffer.Scope!;
            if (scope.IsBacklogged)
                return false;

            scope.Spawn(new MorselWork<ScopedActionSink>(run.CloneForMorsel(), cur, updates, new ScopedActionSink(buffer.CreateTaskLocal())));
            return true;
        }

        /// <inheritdoc />
        public void FinishTask()
        {
            if (buffer.NeedsFlush)
                buffer.FlushOnClone();
        }

    }

}
