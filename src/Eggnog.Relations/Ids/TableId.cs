using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a table in a <see cref="Database"/>.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    internal readonly record struct TableId(int Rep) : INumericId<TableId>
    {
        /// <summary>
        /// A placeholder table identifier, used for synthetic atoms during planning; never names a real table.
        /// </summary>
        public static TableId Dummy => new(int.MaxValue);


        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TableId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "t" + Rep;

    }

}
