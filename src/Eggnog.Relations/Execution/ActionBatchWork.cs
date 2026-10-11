using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A full action batch handed to another thread: the task <c>ScopedActionBuffer::push_bindings</c> spawns
    /// (core-relations/src/free_join/execute.rs:1989-1998). It runs the program on its own execution state, flushes
    /// the state and counts the surviving matches.
    /// </summary>
    internal sealed class ActionBatchWork : ScopedWork
    {

        readonly ExecutionState state;
        readonly ActionInfo action;
        readonly Bindings bindings;
        readonly MatchCounter counter;
        readonly int actionIndex;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="state">A fresh clone of the root state, owned by the task.</param>
        /// <param name="action">The action's program.</param>
        /// <param name="bindings">The full batch, owned by the task.</param>
        /// <param name="counter">The match counter.</param>
        /// <param name="actionIndex">The action's index.</param>
        public ActionBatchWork(ExecutionState state, ActionInfo action, Bindings bindings, MatchCounter counter, int actionIndex)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(bindings);
            ArgumentNullException.ThrowIfNull(counter);
            this.state = state;
            this.action = action;
            this.bindings = bindings;
            this.counter = counter;
            this.actionIndex = actionIndex;
        }

        /// <inheritdoc />
        public override void Run()
        {
            var succeeded = state.RunInstrs(action.Instrs, bindings);
            state.Flush();
            counter.Add(actionIndex, succeeded);
        }

    }

}
