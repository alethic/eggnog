using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The materializer sink of the serial executor: egglog's <c>InPlaceMaterializer</c>
    /// (core-relations/src/free_join/execute.rs:2111-2171). Each match of block <c>i</c> appends the values of the
    /// block's value variables (or one stale cell when it has none) under the values of its message variables in
    /// materialization <c>i</c>.
    /// </summary>
    internal readonly struct InPlaceMaterializerSink : IJoinSink
    {

        readonly Materialization[] materializations;
        readonly MatSpec[] specs;
        readonly uint[] key;
        readonly uint[] value;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="materializations">The materializations being filled, by block index; shared, not copied.</param>
        /// <param name="specs">The block specs, by block index.</param>
        public InPlaceMaterializerSink(Materialization[] materializations, MatSpec[] specs)
        {
            ArgumentNullException.ThrowIfNull(materializations);
            ArgumentNullException.ThrowIfNull(specs);
            this.materializations = materializations;
            this.specs = specs;
            key = new uint[MaxLength(specs, value: false)];
            value = new uint[MaxLength(specs, value: true)];
        }

        /// <inheritdoc />
        public bool SupportsParallelDrain => false;

        /// <inheritdoc />
        public int MorselSize(int level, int total) => JoinRun.DefaultMorselSize;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PushBindings(JoinRun run, int target) => Materialize(run.Values, specs[target], materializations[target], key, value);

        /// <inheritdoc />
        public bool TryRecur(JoinRun run, int cur, FrameUpdates updates) => throw new InvalidOperationException("An in-place sink does not hand off work.");

        /// <inheritdoc />
        public void FinishTask()
        {

        }

        /// <summary>
        /// Gets the scratch length a set of specs needs: the longest message (or value) variable list, at least one.
        /// </summary>
        /// <param name="specs">The specs.</param>
        /// <param name="value">Whether to measure the value variables.</param>
        /// <returns>The length.</returns>
        internal static int MaxLength(MatSpec[] specs, bool value)
        {
            var max = 1;
            foreach (var spec in specs)
                max = Math.Max(max, value ? spec.ValueVariables.Length : spec.MessageVariables.Length);

            return max;
        }

        /// <summary>
        /// Appends the match in <paramref name="values"/> to <paramref name="materialization"/> as <paramref name="spec"/>
        /// says. egglog's materializer <c>push_bindings</c>.
        /// </summary>
        /// <param name="values">The join's bindings, indexed by variable.</param>
        /// <param name="spec">The block's spec.</param>
        /// <param name="materialization">The block's materialization.</param>
        /// <param name="key">Scratch for the key.</param>
        /// <param name="value">Scratch for the row.</param>
        internal static void Materialize(uint[] values, MatSpec spec, Materialization materialization, uint[] key, uint[] value)
        {
            var msg = spec.MessageVariables;
            for (var i = 0; i < msg.Length; i++)
                key[i] = values[msg[i].Index];

            var val = spec.ValueVariables;
            if (val.Length == 0)
            {
                value[0] = Value.StaleRep;
            }
            else
            {
                for (var i = 0; i < val.Length; i++)
                    value[i] = values[val[i].Index];
            }

            materialization.Add(key, value);
        }

    }

}
