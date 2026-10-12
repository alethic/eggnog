using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a shard of a sharded hash table.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    internal readonly record struct ShardId(int Rep) : INumericId<ShardId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ShardId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "s" + Rep;

    }

}
