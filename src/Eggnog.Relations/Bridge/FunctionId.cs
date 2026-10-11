using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a function declared in an <see cref="EGraph"/>: egglog-bridge's <c>FunctionId</c>
    /// (egglog-bridge/src/lib.rs:105). Function ids are dense, in declaration order, and never reused.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    public readonly record struct FunctionId(int Rep) : INumericId<FunctionId>
    {

        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FunctionId FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "fn" + Rep;

    }

}
