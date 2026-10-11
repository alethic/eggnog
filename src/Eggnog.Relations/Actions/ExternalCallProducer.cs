using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Calls an external function once per live lane on the lane's arguments: the closure of egglog's
    /// <c>invoke_batch</c> and <c>invoke_batch_assign</c> (core-relations/src/free_join/mod.rs:215-253). One virtual call
    /// per lane, as egglog's <c>dyn ExternalFunction</c>; the function sees the calling execution state, through which
    /// it may stage writes, read tables, mint ids or call other functions.
    /// </summary>
    internal readonly struct ExternalCallProducer : ILaneProducer
    {

        readonly ExternalFunction function;
        readonly ExecutionState state;
        readonly ArgGather args;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="state">The calling execution state.</param>
        /// <param name="args">Gathers the arguments.</param>
        public ExternalCallProducer(ExternalFunction function, ExecutionState state, ArgGather args)
        {
            this.function = function;
            this.state = state;
            this.args = args;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryProduce(int lane, out uint value)
        {
            args.Load(lane);
            return function.TryInvoke(state, args.Cells, 0, args.Count, out value);
        }

    }

}
