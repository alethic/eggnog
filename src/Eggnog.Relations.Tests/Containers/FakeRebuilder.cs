using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A tiny rebuilder that isolates outer-id canonicalization from inner container rewrites: egglog's
    /// <c>FakeRebuilder</c> (core-relations/src/containers/tests.rs:35-89). <see cref="Rebuilder.RebuildValue"/> maps one
    /// outer id to another; <see cref="Rebuilder.RebuildSlice"/> maps one inner value to another. No hint column, so a
    /// rebuild through it is always full.
    /// </summary>
    sealed class FakeRebuilder : Rebuilder
    {

        readonly uint? oldOuterId;
        readonly uint? newOuterId;
        readonly uint? oldInnerValue;
        readonly uint? newInnerValue;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public FakeRebuilder(uint? oldOuterId, uint? newOuterId, uint? oldInnerValue, uint? newInnerValue)
        {
            this.oldOuterId = oldOuterId;
            this.newOuterId = newOuterId;
            this.oldInnerValue = oldInnerValue;
            this.newInnerValue = newInnerValue;
        }

        /// <inheritdoc />
        public override ColumnId? HintColumn => null;

        /// <inheritdoc />
        public override uint RebuildValue(uint value) => oldOuterId is { } old && newOuterId is { } replacement && value == old ? replacement : value;

        /// <inheritdoc />
        public override void RebuildBuffer(RowBuffer buffer, int start, int end, TaggedRowBuffer output, ExecutionState state)
        {
            throw new InvalidOperationException("FakeRebuilder does not support RebuildBuffer.");
        }

        /// <inheritdoc />
        public override void RebuildSubset(Table table, SubsetRef subset, TaggedRowBuffer output, ExecutionState state)
        {
            throw new InvalidOperationException("FakeRebuilder does not support RebuildSubset.");
        }

        /// <inheritdoc />
        public override bool RebuildSlice(Span<uint> values)
        {
            var changed = false;
            if (oldInnerValue is not { } old || newInnerValue is not { } replacement)
                return false;

            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] == old)
                {
                    values[i] = replacement;
                    changed = true;
                }
            }

            return changed;
        }

    }

}
