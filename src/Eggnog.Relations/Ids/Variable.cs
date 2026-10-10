using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Identifies a variable of a query.
    /// </summary>
    /// <param name="Rep">The raw index.</param>
    public readonly record struct Variable(int Rep) : INumericId<Variable>
    {
        /// <summary>
        /// A placeholder variable that marks a wildcard column: the column is not bound to anything.
        /// </summary>
        public static Variable Placeholder => new(int.MaxValue);

        /// <summary>
        /// Gets whether this is the wildcard placeholder.
        /// </summary>
        public bool IsPlaceholder => Rep == int.MaxValue;


        /// <inheritdoc />
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Rep;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Variable FromIndex(int index) => new(index);

        /// <inheritdoc />
        public override string ToString() => "v" + Rep;

    }

}
