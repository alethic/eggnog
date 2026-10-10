using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A 32-bit value stored in a table cell: an e-class identifier, an interned base value, a container, or a
    /// timestamp. What a value means is decided by the column it sits in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="uint.MaxValue"/> is reserved as <see cref="Stale"/>: a physical row whose first cell holds it has
    /// been deleted. No live value in a table's first column may equal it.
    /// </para>
    /// <para>
    /// Hot paths store and compare raw <see cref="uint"/>s; this wrapper exists for API edges.
    /// </para>
    /// </remarks>
    /// <param name="Rep">The raw representation.</param>
    public readonly record struct Value(uint Rep) : INumericId<Value>
    {

        /// <summary>
        /// The raw representation of the stale marker.
        /// </summary>
        public const uint StaleRep = uint.MaxValue;

        /// <summary>
        /// The stale marker: a value that is never meant to be read.
        /// </summary>
        public static Value Stale => new(StaleRep);

        /// <summary>
        /// Gets whether this is the stale marker.
        /// </summary>
        public bool IsStale
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep == StaleRep;
        }

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (int)Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Value FromIndex(int index) => new((uint)index);

        /// <summary>
        /// Converts a raw representation to a value.
        /// </summary>
        /// <param name="rep">The raw representation.</param>
        public static implicit operator Value(uint rep) => new(rep);

        /// <summary>
        /// Converts a value to its raw representation.
        /// </summary>
        /// <param name="value">The value.</param>
        public static implicit operator uint(Value value) => value.Rep;

        /// <inheritdoc />
        public override string ToString() => IsStale ? "stale" : Rep.ToString();

    }

}
