using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a column of a table, counting key columns first and then value columns.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    public readonly record struct ColumnId(int Rep) : INumericId<ColumnId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ColumnId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "c" + Rep;

    }

}
