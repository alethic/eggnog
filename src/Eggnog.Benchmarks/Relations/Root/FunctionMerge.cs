using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A merge function that calls a constructor on the two values it merges: egglog-bridge's
    /// <c>ResolvedMergeFn::Function</c> with the arguments <c>old</c> and <c>new</c> (egglog-bridge/src/lib.rs:1218-1240),
    /// the merge of <c>(function f (...) T :merge (Ctor old new))</c>. When the values differ, the merged value is
    /// <c>Ctor(old, new)</c>, looked up or created through <see cref="ExecutionState.PredictRow(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, Span{uint})"/>
    /// with a fresh id and the clock's timestamp, as the bridge's <c>TableAction::lookup_or_insert</c> does
    /// (lib.rs:1307-1330).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor's table must be a read dependency of the merging table, so it is merged (not taken out) when
    /// this merge runs. The predicted row is staged into the constructor and merged in the next round of the same
    /// <see cref="Database.MergeAll"/>.
    /// </para>
    /// <para>
    /// Not a lattice merge: a child context refuses to create a layer of a shared (not context-private) table merged by
    /// it, so only a root writes such a table. Allocation-free: the key, the values and the predicted row live on the
    /// stack.
    /// </para>
    /// </remarks>
    internal sealed class FunctionMerge : MergeFunction
    {

        readonly TableId constructor;
        readonly CounterId ids;
        readonly CounterId clock;
        readonly int valueColumn;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="constructor">The constructor's table: two id key columns, the id output, the timestamp.</param>
        /// <param name="ids">The counter fresh ids come from.</param>
        /// <param name="clock">The timestamp counter.</param>
        /// <param name="valueColumn">The merging table's value column.</param>
        public FunctionMerge(TableId constructor, CounterId ids, CounterId clock, int valueColumn)
            : base(false)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(valueColumn);

            this.constructor = constructor;
            this.ids = ids;
            this.clock = clock;
            this.valueColumn = valueColumn;
        }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            // egglog-bridge github.com/egraphs-good/egglog/pull/287: equal values merge to themselves.
            var cur = current[valueColumn];
            var inc = incoming[valueColumn];
            if (cur == inc)
                return false;

            var ts = (uint)state.ReadCounter(clock);
            ReadOnlySpan<uint> key = [cur, inc];
            ReadOnlySpan<MergeVal> values = [MergeVal.FromCounter(ids), MergeVal.FromConstant(new Value(ts))];
            Span<uint> row = stackalloc uint[4];
            state.PredictRow(constructor, key, values, row);
            var merged = row[2];
            incoming.CopyTo(output);
            output[valueColumn] = merged;
            return merged != cur;
        }

    }

}
