using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a term in a <see cref="TermDag"/>: egglog's <c>TermId</c> (src/termdag.rs:5), an index into the DAG's
    /// arena. Ids are dense, in insertion order, and only meaningful in the DAG that issued them.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    internal readonly record struct TermId(int Rep) : INumericId<TermId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TermId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "term" + Rep;

    }

}
