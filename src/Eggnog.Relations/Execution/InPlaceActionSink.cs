using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The action sink of the serial executor: egglog's <c>InPlaceActionBuffer</c>
    /// (core-relations/src/free_join/execute.rs:1882-1941). Matches are batched per action and full batches run inline;
    /// drains never hand off.
    /// </summary>
    internal readonly struct InPlaceActionSink : IJoinSink
    {

        readonly ActionBuffer buffer;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="buffer">The batches; a buffer without a scope.</param>
        public InPlaceActionSink(ActionBuffer buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (buffer.Scope is not null)
                throw new ArgumentException("An in-place sink runs batches inline.", nameof(buffer));

            this.buffer = buffer;
        }

        /// <summary>
        /// Gets the batches.
        /// </summary>
        public ActionBuffer Buffer => buffer;

        /// <inheritdoc />
        public bool SupportsParallelDrain => false;

        /// <inheritdoc />
        public int MorselSize(int level, int total) => JoinRun.DefaultMorselSize;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PushBindings(JoinRun run, int target) => buffer.Push(target, run.Values, run.BoundVariables);

        /// <inheritdoc />
        public bool TryRecur(JoinRun run, int cur, FrameUpdates updates) => throw new InvalidOperationException("An in-place sink does not hand off work.");

        /// <inheritdoc />
        public void FinishTask()
        {

        }

    }

}
