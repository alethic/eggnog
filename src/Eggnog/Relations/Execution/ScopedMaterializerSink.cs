using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The materializer sink of the parallel executor: egglog's <c>ScopedMaterializer</c>
    /// (core-relations/src/free_join/execute.rs:2173-2245). Matches go to the calling thread's part of the block's
    /// <see cref="ConcurrentMaterialization"/>; drains at positions 0 and 1 hand their frames to tasks of the block's
    /// scope, each with its own sink and scratch.
    /// </summary>
    internal readonly struct ScopedMaterializerSink : IJoinSink
    {

        readonly WorkScope scope;
        readonly ConcurrentMaterialization[] materializations;
        readonly MatSpec[] specs;
        readonly uint[] key;
        readonly uint[] value;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scope">The block's scope.</param>
        /// <param name="materializations">The materializations being filled, by block index; shared, not copied.</param>
        /// <param name="specs">The block specs, by block index.</param>
        public ScopedMaterializerSink(WorkScope scope, ConcurrentMaterialization[] materializations, MatSpec[] specs)
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(materializations);
            ArgumentNullException.ThrowIfNull(specs);
            this.scope = scope;
            this.materializations = materializations;
            this.specs = specs;
            key = new uint[InPlaceMaterializerSink.MaxLength(specs, value: false)];
            value = new uint[InPlaceMaterializerSink.MaxLength(specs, value: true)];
        }

        /// <inheritdoc />
        public bool SupportsParallelDrain => true;

        /// <inheritdoc />
        public int MorselSize(int level, int total) => JoinRun.DefaultMorselSize;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PushBindings(JoinRun run, int target) => InPlaceMaterializerSink.Materialize(run.Values, specs[target], materializations[target].Local, key, value);

        /// <inheritdoc />
        public bool TryRecur(JoinRun run, int cur, FrameUpdates updates)
        {
            if (scope.IsBacklogged)
                return false;

            scope.Spawn(new MorselWork<ScopedMaterializerSink>(run.CloneForMorsel(), cur, updates, new ScopedMaterializerSink(scope, materializations, specs)));
            return true;
        }

        /// <inheritdoc />
        public void FinishTask()
        {

        }

    }

}
