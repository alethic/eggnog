using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A morsel of frames handed to another task: the closure egglog's <c>drain_updates_parallel</c> passes to
    /// <c>recur</c> (core-relations/src/free_join/execute.rs:1031-1089, 2011-2040). The task replays the frames on its
    /// own copy of the join's state, recursing into the next stage after each, then lets its sink finish.
    /// </summary>
    /// <typeparam name="TSink">The sink type, so the replay is specialized for it.</typeparam>
    internal sealed class MorselWork<TSink> : ScopedWork
        where TSink : struct, IJoinSink
    {

        readonly JoinRun run;
        readonly int cur;
        readonly FrameUpdates updates;
        TSink sink;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="run">The task's copy of the join (<see cref="JoinRun.CloneForMorsel"/>).</param>
        /// <param name="cur">The position the frames were produced at.</param>
        /// <param name="updates">The frames, owned by the task.</param>
        /// <param name="sink">The task's sink.</param>
        public MorselWork(JoinRun run, int cur, FrameUpdates updates, TSink sink)
        {
            ArgumentNullException.ThrowIfNull(run);
            ArgumentNullException.ThrowIfNull(updates);
            this.run = run;
            this.cur = cur;
            this.updates = updates;
            this.sink = sink;
        }

        /// <inheritdoc />
        public override void Run()
        {
            run.ReplayMorsel(cur, updates, ref sink);
            sink.FinishTask();
        }

    }

}
